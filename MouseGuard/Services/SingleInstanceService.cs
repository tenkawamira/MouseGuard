using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Windows;

namespace MouseGuard.Services;

public sealed class SingleInstanceService : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private bool _ownsMutex;

    public event EventHandler? ActivationRequested;

    public SingleInstanceService()
    {
        var user = WindowsIdentity.GetCurrent().User?.Value ?? "default";
        var safeUser = user.Replace("-", string.Empty);
        _pipeName = $"MouseGuard.Activate.{safeUser}";
        _mutex = new Mutex(true, $@"Local\MouseGuard.SingleInstance.{safeUser}", out var createdNew);
        _ownsMutex = createdNew;
    }

    public bool IsPrimary => _ownsMutex;

    public void StartActivationServer()
    {
        if (!_ownsMutex)
            return;

        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(_cts.Token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var message = await reader.ReadLineAsync();
                    if (string.Equals(message, "SHOW", StringComparison.Ordinal))
                    {
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                            ActivationRequested?.Invoke(this, EventArgs.Empty));
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(250);
                }
            }
        });
    }

    public bool TryActivateExisting()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            client.Connect(700);
            using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            writer.WriteLine("SHOW");
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool WaitForExistingToExit(TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < timeout)
        {
            try
            {
                using var existing = Mutex.OpenExisting($@"Local\MouseGuard.SingleInstance.{GetUserSuffix()}");
                Thread.Sleep(150);
            }
            catch
            {
                // The old instance released the mutex.
                _ownsMutex = _mutex.WaitOne(0);
                return _ownsMutex;
            }
        }

        return false;
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
            }

            _ownsMutex = false;
        }

        _mutex.Dispose();
        _cts.Dispose();
    }

    private static string GetUserSuffix()
    {
        var user = WindowsIdentity.GetCurrent().User?.Value ?? "default";
        return user.Replace("-", string.Empty);
    }
}
