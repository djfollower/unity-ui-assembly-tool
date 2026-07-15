using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;

namespace UiAssemblerSlice.Editor.Review
{
    /// Runs a sequence of external processes (the Node CLI's
    /// reduce-from-selection -> match chain, from ReviewWindow) without
    /// blocking the Editor UI thread. Polls Process.HasExited via
    /// EditorApplication.update rather than subscribing to Process.Exited
    /// (which fires off-thread) - simpler and avoids ever touching Unity
    /// API from a non-main thread. OutputDataReceived/ErrorDataReceived
    /// also fire on a ThreadPool thread, so those callbacks only enqueue
    /// plain strings into a lock-guarded queue; the update tick drains the
    /// queue and invokes the caller's callbacks on the main thread, the
    /// only place that's safe.
    public class PipelineRunner
    {
        public bool IsRunning { get; private set; }

        private readonly object _lock = new object();
        private readonly Queue<string> _pending = new Queue<string>();

        private List<(string fileName, string arguments, string workingDirectory)> _steps;
        private int _stepIndex;
        private Process _current;
        private Action<string> _onLogLine;
        private Action _onAllFinished;
        private Action<string> _onFailed;

        public void RunSteps(
            List<(string fileName, string arguments, string workingDirectory)> steps,
            Action<string> onLogLine,
            Action onAllFinished,
            Action<string> onFailed)
        {
            if (IsRunning) return;

            _steps = steps;
            _stepIndex = 0;
            _onLogLine = onLogLine;
            _onAllFinished = onAllFinished;
            _onFailed = onFailed;

            IsRunning = true;
            EditorApplication.update += Tick;
            StartNextStep();
        }

        public void Stop()
        {
            IsRunning = false;
            EditorApplication.update -= Tick;
            // Defensive try/catch, not just a HasExited guard: HasExited
            // itself throws InvalidOperationException on a Process whose
            // Start() never succeeded (e.g. Stop() called from
            // StartNextStep's catch block below) - best-effort cleanup
            // either way, this must never throw back into a UI callback.
            try
            {
                if (_current != null && !_current.HasExited) _current.Kill();
            }
            catch (Exception)
            {
                // ignored - already gone, or never started
            }
            _current?.Dispose();
            _current = null;
        }

        private void StartNextStep()
        {
            var (fileName, arguments, workingDirectory) = _steps[_stepIndex];
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            _current = new Process { StartInfo = psi, EnableRaisingEvents = false };
            _current.OutputDataReceived += (_, e) => { if (e.Data != null) Enqueue(e.Data); };
            _current.ErrorDataReceived += (_, e) => { if (e.Data != null) Enqueue(e.Data); };

            try
            {
                _current.Start();
                _current.BeginOutputReadLine();
                _current.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                var failedFileName = fileName;
                Stop();
                _onFailed($"failed to start '{failedFileName}': {ex.Message}");
            }
        }

        private void Enqueue(string line)
        {
            lock (_lock) _pending.Enqueue(line);
        }

        private void Tick()
        {
            if (_current == null) return;

            DrainQueue();
            if (!_current.HasExited) return;

            var exitCode = _current.ExitCode;
            _current.Dispose();
            _current = null;

            if (exitCode != 0)
            {
                var failedStep = _stepIndex + 1;
                Stop();
                _onFailed($"step {failedStep} exited with code {exitCode}");
                return;
            }

            _stepIndex++;
            if (_stepIndex >= _steps.Count)
            {
                Stop();
                _onAllFinished();
            }
            else
            {
                StartNextStep();
            }
        }

        private void DrainQueue()
        {
            List<string> lines = null;
            lock (_lock)
            {
                if (_pending.Count > 0)
                {
                    lines = new List<string>(_pending);
                    _pending.Clear();
                }
            }
            if (lines == null) return;
            foreach (var line in lines) _onLogLine(line);
        }
    }
}
