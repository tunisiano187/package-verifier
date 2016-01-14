// Copyright © 2015 - Present RealDimensions Software, LLC
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// 
// You may obtain a copy of the License at
// 
// 	http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

namespace chocolatey.package.verifier.infrastructure.app.services
{
    using System;
    using System.Diagnostics;
    using System.Reflection;
    using System.Text;
    using System.Threading;
    using commands;
    using configuration;
    using filesystem;
    using infrastructure.results;
    using logging;
    using results;

    public class TurboService : IPackageTestService
    {
        private readonly ICommandExecutor _commandExecutor;
        private readonly IFileSystem _fileSystem;
        private readonly IConfigurationSettings _configuration;
        private readonly string _turboExecutable = @"C:\Program Files (x86)\Spoon\Cmd\turbo.exe";
        private const string CONTAINER_NAME = "chocotest";

        private Process _containerProcess;

        private AutoResetEvent _logEvent = new AutoResetEvent(false);

        public TurboService()
        {
            _fileSystem = new DotNetFileSystem();
            _commandExecutor = new CommandExecutor(_fileSystem);
            _configuration = new ConfigurationSettings();
            if (!_fileSystem.file_exists(_turboExecutable))
            {
                _turboExecutable = _fileSystem.get_executable_path("turbo.exe");
            }
        }

        public void run_std_in_test()
        {
            Log.InitializeWith(new ConsoleLog());

            var stdOut = _commandExecutor.execute_redirect_stdin(
               "powershell.exe",
               "-Command -",
               _fileSystem.get_directory_name(Assembly.GetExecutingAssembly().Location),
               (s, e) =>
               {
                   if (e == null || string.IsNullOrWhiteSpace(e.Data)) return;
                   this.Log().Info(() => " [Turbo] {0}".format_with(e.Data));
                   //todo: need to determine if more logging events are coming and hold until the command has finished
                   _logEvent.Set();
               },
               (s, e) =>
               {
                   if (e == null || string.IsNullOrWhiteSpace(e.Data)) return;
                   this.Log().Warn(() => " [Turbo][Error] {0}".format_with(e.Data));
                   //todo: need to determine if more logging events are coming and hold until the command has finished
                   _logEvent.Reset();
               },
               updateProcessPath: false,
               allowUseWindow: false);

            this.Log().Info("writing 'write-host hi'");
            stdOut.WriteLine("write-host hi");
            stdOut.Flush();
            _logEvent.WaitOne();

            //this.Log().Info("sleeping");
            //Thread.Sleep(5000);
            //this.Log().Info("waking");

            this.Log().Info("writing 'write-host bye'");
            stdOut.WriteLine("write-host bye;write-host nooooo");
            stdOut.Flush();
            _logEvent.WaitOne(5000);

            this.Log().Info("closing");
            stdOut.Close();
            this.Log().Info("closed");
            stdOut.Dispose();
            this.Log().Info("disposed");
            var dude = 1;
        }

        public void run_tests()
        {
            Log.InitializeWith(new ConsoleLog());

            start_container();
            // prep();
            // execute_in_turbo("choco.exe install Compass --version 1.0.3 -x86 -fdvy");
        }

        public TurboService(ICommandExecutor commandExecutor, IFileSystem fileSystem, IConfigurationSettings configuration)
        {
            _commandExecutor = commandExecutor;
            _fileSystem = fileSystem;
            _configuration = configuration;

            if (!_fileSystem.file_exists(_turboExecutable))
            {
                _turboExecutable = _fileSystem.get_executable_path("turbo.exe");
            }
        }

        private bool is_running()
        {
            return _containerProcess != null;
        }

        private Process start_container()
        {
            shutdown();
            // mount internal directory for files and logs
            var command = "run chocolateylatest --admin -n={0} --mount=\"{1}\" choco.exe install Compass --version 1.0.3 -x86 -fdvy".format_with(CONTAINER_NAME, _fileSystem.get_full_path(".\\files"));
            var logs = new StringBuilder();
            var results = new TestCommandOutputResult();

            var psi = new ProcessStartInfo(_turboExecutable, command)
            {
                UseShellExecute = false,
                WorkingDirectory = _fileSystem.get_directory_name(Assembly.GetExecutingAssembly().Location),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Minimized,
            };

            var p = new Process();
            p.StartInfo = psi;
            p.OutputDataReceived += (s, e) =>
            {
                if (e == null || string.IsNullOrWhiteSpace(e.Data)) return;
                this.Log().Info(() => " [Container] {0}".format_with(e.Data));
                logs.AppendLine(e.Data);
                results.Messages.Add(
                    new ResultMessage
                    {
                        Message = e.Data,
                        MessageType = ResultType.Note
                    });
            };
            p.ErrorDataReceived += (s, e) =>
            {
                if (e == null || string.IsNullOrWhiteSpace(e.Data)) return;
                this.Log().Warn(() => " [Container][Error] {0}".format_with(e.Data));
                logs.AppendLine("[ERROR] " + e.Data);
                results.Messages.Add(
                    new ResultMessage
                    {
                        Message = e.Data,
                        MessageType = ResultType.Note
                    });
            };
            p.EnableRaisingEvents = true;
            p.Exited += (sender, args) => { _containerProcess = null; };

            p.Start();
            p.BeginErrorReadLine();
            p.BeginOutputReadLine();

           // p.StandardInput.WriteLine("choco.exe install Compass --version 1.0.3 -x86 -fdvy");
           // p.StandardInput.Flush();

            this.Log().Debug("Waiting results");

            //while (!p.StandardOutput.EndOfStream)
            //{
            //    this.Log().Info(() => " [Container] {0}".format_with(p.StandardOutput.ReadLine()));
            //}

            p.WaitForExit();

            return p;

            // turbo start -a --admin test
        }

        public bool prep()
        {
            if (is_running()) return true;

            _containerProcess = start_container();
            Thread.Sleep(1000);

            //todo: * del logs / .chocolatey / etc and create symlinks to folders under .files

            return true;
        }

        public bool reset()
        {
            shutdown();
            var reset = execute_turbo("revert {0}".format_with(CONTAINER_NAME)).Logs.to_lower().Contains("reverted all changes");
            prep();

            return reset;
        }

        public TestCommandOutputResult run(string command)
        {
            return execute_in_turbo(command); //.Result;
        }

        public void shutdown()
        {
            // turbo suspend
            execute_turbo("stop {0}".format_with(CONTAINER_NAME));

            if (_containerProcess == null) return;
            try
            {
                if (!_containerProcess.HasExited)
                {
                    _containerProcess.Kill();
                }
            }
            catch (Exception)
            {
                // nothing to see here
            }
            finally
            {
                _containerProcess = null;
            }
        }

        public void destroy()
        {
            shutdown();
            execute_turbo("rm {0}".format_with(CONTAINER_NAME));
        }

        private TestCommandOutputResult execute_turbo(string command)
        {
            this.Log().Debug(() => "Executing turbo command '{0}'.".format_with(command.escape_curly_braces()));
            var results = new TestCommandOutputResult();
            var logs = new StringBuilder();

            var output = _commandExecutor.execute(
                _turboExecutable,
                command,
                _configuration.CommandExecutionTimeoutSeconds + 60,
                _fileSystem.get_directory_name(Assembly.GetExecutingAssembly().Location),
                (s, e) =>
                {
                    if (e == null || string.IsNullOrWhiteSpace(e.Data)) return;
                    this.Log().Info(() => " [Turbo] {0}".format_with(e.Data));
                    logs.AppendLine(e.Data);
                    results.Messages.Add(
                        new ResultMessage
                        {
                            Message = e.Data,
                            MessageType = ResultType.Note
                        });
                },
                (s, e) =>
                {
                    if (e == null || string.IsNullOrWhiteSpace(e.Data)) return;
                    this.Log().Warn(() => " [Turbo][Error] {0}".format_with(e.Data));
                    logs.AppendLine("[ERROR] " + e.Data);
                    results.Messages.Add(
                        new ResultMessage
                        {
                            Message = e.Data,
                            MessageType = ResultType.Note
                        });
                },
                updateProcessPath: false,
                allowUseWindow: false);

            results.Logs = logs.ToString();
            results.ExitCode = output;

            return results;
        }

        private TestCommandOutputResult execute_in_turbo(string command)
        {
            this.Log().Debug(() => "Executing command in turbo container: '{0}'.".format_with(command.escape_curly_braces()));

            var results = new TestCommandOutputResult();

            _containerProcess.StandardInput.WriteLine(command);
            _containerProcess.StandardInput.Flush();

            this.Log().Debug("Waiting results");

            while (!_containerProcess.StandardOutput.EndOfStream)
            {
                this.Log().Info(() => " [Container] {0}".format_with(_containerProcess.StandardOutput.ReadLine()));
            }

            //  await _containerProcess.StandardOutput.ReadToEndAsync();

            //while (!_containerProcess.StandardOutput.EndOfStream)
            //{
            //    _containerProcess.StandardOutput.ReadLineAsync();
            //}

            return results;
        }
    }
}
