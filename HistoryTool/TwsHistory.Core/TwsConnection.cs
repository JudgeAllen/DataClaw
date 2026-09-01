using System;
using System.Threading;
using IBApi;

namespace TwsHistory.Core
{
    /// <summary>
    /// Establishes a TWS/Gateway API connection and runs the message-pump
    /// reader thread. Shared by the bar fetcher and the diagnostic probes.
    /// </summary>
    public sealed class TwsConnection : IDisposable
    {
        private readonly TwsWrapper wrapper;
        private readonly EReaderMonitorSignal signal = new EReaderMonitorSignal();
        private EClientSocket client;

        public TwsWrapper Wrapper => wrapper;
        public EClientSocket Client => client;

        public TwsConnection(CliOptions opt, Action<string> error)
        {
            wrapper = new TwsWrapper(error);
        }

        /// <summary>Connects and starts the reader. Returns false (with details via info/error) on failure.</summary>
        public bool Connect(CliOptions opt, Action<string> info)
        {
            client = new EClientSocket(wrapper, signal);
            info($"Connecting to {opt.Host}:{opt.Port} (clientId {opt.ClientId}) ...");
            client.eConnect(opt.Host, opt.Port, opt.ClientId);

            var reader = new EReader(client, signal);
            reader.Start();
            var readerThread = new Thread(() =>
            {
                while (client.IsConnected())
                {
                    try
                    {
                        signal.waitForSignal();
                        reader.processMsgs();
                    }
                    catch (Exception ex)
                    {
                        // Never let a decoder hiccup kill the whole process
                        // (it used to terminate the GUI silently).
                        wrapper.NotifyError("reader exception: " + ex.Message);
                        try { client.eDisconnect(); } catch { /* ignore */ }
                        break;
                    }
                }
            })
            { IsBackground = true, Name = "api-reader" };
            readerThread.Start();

            if (!wrapper.WaitUntilConnected(TimeSpan.FromSeconds(opt.ConnectTimeoutSec)))
            {
                info("Connection failed: " + wrapper.LastErrorText);
                return false;
            }
            info($"Connected. Server version {client.ServerVersion}, time {client.ServerTime}.");
            return true;
        }

        public void Dispose()
        {
            try { client?.eDisconnect(); }
            catch { /* ignore */ }
        }
    }
}
