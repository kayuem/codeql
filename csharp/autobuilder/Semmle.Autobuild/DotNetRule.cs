using System;
using Semmle.Util.Logging;
using System.Linq;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.IO;
using Semmle.Util;
using System.Text.RegularExpressions;

namespace Semmle.Autobuild
{
    /// <summary>
    /// A build rule where the build command is of the form "dotnet build".
    /// Currently unused because the tracer does not work with dotnet.
    /// </summary>
    class DotNetRule : IBuildRule
    {
        public BuildScript Analyse(Autobuilder builder, bool auto)
        {
            if (!builder.ProjectsOrSolutionsToBuild.Any())
                return BuildScript.Failure;

            if (auto)
            {
                var notDotNetProject = builder.ProjectsOrSolutionsToBuild.
                  SelectMany(p => Enumerators.Singleton(p).Concat(p.IncludedProjects)).
                  OfType<Project>().
                  FirstOrDefault(p => !p.DotNetProject);
                if (notDotNetProject != null)
                {
                    builder.Log(Severity.Info, "Not using .NET Core because of incompatible project {0}", notDotNetProject);
                    return BuildScript.Failure;
                }

                builder.Log(Severity.Info, "Attempting to build using .NET Core");
            }

            return WithDotNet(builder, dotNet =>
                {
                    var ret = GetInfoCommand(builder.Actions, dotNet);
                    foreach (var projectOrSolution in builder.ProjectsOrSolutionsToBuild)
                    {
                        var cleanCommand = GetCleanCommand(builder.Actions, dotNet);
                        cleanCommand.QuoteArgument(projectOrSolution.FullPath);
                        var clean = cleanCommand.Script;

                        var restoreCommand = GetRestoreCommand(builder.Actions, dotNet);
                        restoreCommand.QuoteArgument(projectOrSolution.FullPath);
                        var restore = restoreCommand.Script;

                        var build = GetBuildScript(builder, dotNet, projectOrSolution.FullPath);

                        ret &= clean & BuildScript.Try(restore) & build;
                    }
                    return ret;
                });
        }

        /// <summary>
        /// Returns a script that attempts to download relevant version(s) of the
        /// .NET Core SDK, followed by running the script generated by <paramref name="f"/>.
        ///
        /// The first element <code>DotNetPath</code> of the argument to <paramref name="f"/>
        /// is the path where .NET Core was installed, and the second element <code>Environment</code>
        /// is any additional required environment variables. The tuple argument is <code>null</code>
        /// when the installation failed.
        /// </summary>
        public static BuildScript WithDotNet(Autobuilder builder, Func<(string DotNetPath, IDictionary<string, string> Environment)?, BuildScript> f)
        {
            var installDir = builder.Actions.PathCombine(builder.Options.RootDirectory, ".dotnet");
            var installScript = DownloadDotNet(builder, installDir);
            return BuildScript.Bind(installScript, installed =>
                {
                    if (installed == 0)
                    {
                        // The installation succeeded, so use the newly installed .NET Core
                        var path = builder.Actions.GetEnvironmentVariable("PATH");
                        var delim = builder.Actions.IsWindows() ? ";" : ":";
                        var env = new Dictionary<string, string>{
                            { "DOTNET_MULTILEVEL_LOOKUP", "false" }, // prevent look up of other .NET Core SDKs
                            { "DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "true" },
                            { "PATH", installDir + delim + path }
                        };
                        return f((installDir, env));
                    }

                    return f(null);
                });
        }

        /// <summary>
        /// Returns a script for downloading relevant versions of the
        /// .NET Core SDK. The SDK(s) will be installed at <code>installDir</code>
        /// (provided that the script succeeds).
        /// </summary>
        static BuildScript DownloadDotNet(Autobuilder builder, string installDir)
        {
            if (!string.IsNullOrEmpty(builder.Options.DotNetVersion))
                // Specific version supplied in configuration: always use that
                return DownloadDotNetVersion(builder, installDir, builder.Options.DotNetVersion);

            // Download versions mentioned in `global.json` files
            // See https://docs.microsoft.com/en-us/dotnet/core/tools/global-json
            var installScript = BuildScript.Success;
            var validGlobalJson = false;
            foreach (var path in builder.Paths.Select(p => p.Item1).Where(p => p.EndsWith("global.json", StringComparison.Ordinal)))
            {
                string version;
                try
                {
                    var o = JObject.Parse(File.ReadAllText(path));
                    version = (string)o["sdk"]["version"];
                }
                catch  // lgtm[cs/catch-of-all-exceptions]
                {
                    // not a valid global.json file
                    continue;
                }

                installScript &= DownloadDotNetVersion(builder, installDir, version);
                validGlobalJson = true;
            }

            return validGlobalJson ? installScript : BuildScript.Failure;
        }

        /// <summary>
        /// Returns a script for downloading a specific .NET Core SDK version, if the
        /// version is not already installed.
        ///
        /// See https://docs.microsoft.com/en-us/dotnet/core/tools/dotnet-install-script.
        /// </summary>
        static BuildScript DownloadDotNetVersion(Autobuilder builder, string path, string version)
        {
            return BuildScript.Bind(GetInstalledSdksScript(builder.Actions), (sdks, sdksRet) =>
                {
                    if (sdksRet == 0 && sdks.Count() == 1 && sdks[0].StartsWith(version + " ", StringComparison.Ordinal))
                        // The requested SDK is already installed (and no other SDKs are installed), so
                        // no need to reinstall
                        return BuildScript.Failure;

                    builder.Log(Severity.Info, "Attempting to download .NET Core {0}", version);

                    if (builder.Actions.IsWindows())
                    {
                        var psScript = @"param([string]$Version, [string]$InstallDir)

add-type @""
using System.Net;
using System.Security.Cryptography.X509Certificates;
public class TrustAllCertsPolicy : ICertificatePolicy
{
    public bool CheckValidationResult(ServicePoint srvPoint, X509Certificate certificate, WebRequest request, int certificateProblem)
    {
        return true;
    }
}
""@
$AllProtocols = [System.Net.SecurityProtocolType]'Ssl3,Tls,Tls11,Tls12'
[System.Net.ServicePointManager]::SecurityProtocol = $AllProtocols
[System.Net.ServicePointManager]::CertificatePolicy = New-Object TrustAllCertsPolicy
$Script = Invoke-WebRequest -useb 'https://dot.net/v1/dotnet-install.ps1'

$arguments = @{
  Channel = 'release'
  Version = $Version
  InstallDir = $InstallDir
}

$ScriptBlock = [scriptblock]::create("".{$($Script)} $(&{$args} @arguments)"")

Invoke-Command -ScriptBlock $ScriptBlock";
                        var psScriptFile = builder.Actions.PathCombine(builder.Options.RootDirectory, "install-dotnet.ps1");
                        builder.Actions.WriteAllText(psScriptFile, psScript);

                        var install = new CommandBuilder(builder.Actions).
                            RunCommand("powershell").
                            Argument("-NoProfile").
                            Argument("-ExecutionPolicy").
                            Argument("unrestricted").
                            Argument("-file").
                            Argument(psScriptFile).
                            Argument("-Version").
                            Argument(version).
                            Argument("-InstallDir").
                            Argument(path);

                        var removeScript = new CommandBuilder(builder.Actions).
                            RunCommand("del").
                            Argument(psScriptFile);

                        return install.Script & BuildScript.Try(removeScript.Script);
                    }
                    else
                    {
                        var curl = new CommandBuilder(builder.Actions).
                            RunCommand("curl").
                            Argument("-L").
                            Argument("-sO").
                            Argument("https://dot.net/v1/dotnet-install.sh");

                        var chmod = new CommandBuilder(builder.Actions).
                            RunCommand("chmod").
                            Argument("u+x").
                            Argument("dotnet-install.sh");

                        var install = new CommandBuilder(builder.Actions).
                            RunCommand("./dotnet-install.sh").
                            Argument("--channel").
                            Argument("release").
                            Argument("--version").
                            Argument(version).
                            Argument("--install-dir").
                            Argument(path);

                        var removeScript = new CommandBuilder(builder.Actions).
                            RunCommand("rm").
                            Argument("dotnet-install.sh");

                        return curl.Script & chmod.Script & install.Script & BuildScript.Try(removeScript.Script);
                    }
                });
        }

        static BuildScript GetInstalledSdksScript(IBuildActions actions)
        {
            var listSdks = new CommandBuilder(actions, silent: true).
                RunCommand("dotnet").
                Argument("--list-sdks");
            return listSdks.Script;
        }

        static string DotNetCommand(IBuildActions actions, string dotNetPath) =>
            dotNetPath != null ? actions.PathCombine(dotNetPath, "dotnet") : "dotnet";

        BuildScript GetInfoCommand(IBuildActions actions, (string DotNetPath, IDictionary<string, string> Environment)? arg)
        {
            var info = new CommandBuilder(actions, null, arg?.Environment).
                RunCommand(DotNetCommand(actions, arg?.DotNetPath)).
                Argument("--info");
            return info.Script;
        }

        CommandBuilder GetCleanCommand(IBuildActions actions, (string DotNetPath, IDictionary<string, string> Environment)? arg)
        {
            var clean = new CommandBuilder(actions, null, arg?.Environment).
                RunCommand(DotNetCommand(actions, arg?.DotNetPath)).
                Argument("clean");
            return clean;
        }

        CommandBuilder GetRestoreCommand(IBuildActions actions, (string DotNetPath, IDictionary<string, string> Environment)? arg)
        {
            var restore = new CommandBuilder(actions, null, arg?.Environment).
                RunCommand(DotNetCommand(actions, arg?.DotNetPath)).
                Argument("restore");
            return restore;
        }

        static BuildScript GetInstalledRuntimesScript(IBuildActions actions, (string DotNetPath, IDictionary<string, string> Environment)? arg)
        {
            var listSdks = new CommandBuilder(actions, environment: arg?.Environment, silent: true).
                RunCommand(DotNetCommand(actions, arg?.DotNetPath)).
                Argument("--list-runtimes");
            return listSdks.Script;
        }

        /// <summary>
        /// Gets the `dotnet build` script.
        ///
        /// The CLR tracer only works on .NET Core >= 3 on Linux and macOS (see
        /// https://github.com/dotnet/coreclr/issues/19622), so in case we are
        /// running on an older .NET Core, we disable shared compilation (and
        /// hence the need for CLR tracing), by adding a
        /// `/p:UseSharedCompilation=false` argument.
        /// </summary>
        BuildScript GetBuildScript(Autobuilder builder, (string DotNetPath, IDictionary<string, string> Environment)? arg, string projOrSln)
        {
            var build = new CommandBuilder(builder.Actions, null, arg?.Environment);
            var script = builder.MaybeIndex(build, DotNetCommand(builder.Actions, arg?.DotNetPath)).
                Argument("build").
                Argument("--no-incremental");

            if (builder.Actions.IsWindows())
                return script.Argument(builder.Options.DotNetArguments).
                    QuoteArgument(projOrSln).
                    Script;

            return BuildScript.Bind(GetInstalledRuntimesScript(builder.Actions, arg), (runtimes, runtimesRet) =>
            {
                var compatibleClr = false;
                if (runtimesRet == 0)
                {
                    var regex = new Regex(@"Microsoft\.NETCore\.App (\d)");
                    compatibleClr = runtimes.
                        Select(runtime => regex.Match(runtime)).
                        Where(m => m.Success).
                        Any(m => int.TryParse(m.Groups[1].Value, out var version) && version >= 3);
                }

                return compatibleClr ?
                    script.Argument(builder.Options.DotNetArguments).
                        QuoteArgument(projOrSln).
                        Script :
                    script.Argument("/p:UseSharedCompilation=false").
                        Argument(builder.Options.DotNetArguments).
                        QuoteArgument(projOrSln).
                        Script;
            });
        }
    }
}
