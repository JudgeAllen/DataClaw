using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using IBApi;

namespace TwsHistory.Core
{
    /// <summary>
    /// EWrapper implementation. The IB decoder always routes bars through the
    /// legacy <c>historicalData</c> / <c>historicalDataEnd</c> callbacks even
    /// when the protobuf transport is negotiated, so only those are needed.
    /// </summary>
    public sealed class TwsWrapper : DefaultEWrapper
    {
        private readonly object gate = new object();
        private readonly AutoResetEvent connectedEvent = new AutoResetEvent(false);
        private readonly AutoResetEvent requestEvent = new AutoResetEvent(false);
        private readonly Action<string> errorSink;

        private bool connected;
        private string fatalConnectError;
        private readonly StringBuilder errorLog = new StringBuilder();
        private readonly List<string> pendingErrors = new List<string>();

        private int pendingReqId = -1;
        private PageResult current;

        // Connectivity-level codes that should abort a connect attempt.
        private static readonly HashSet<int> FatalConnectCodes = new HashSet<int> { 502, 504, 507, 509, 1100, 1102, 1300 };

        public TwsWrapper(Action<string> errorSink)
        {
            this.errorSink = errorSink ?? (_ => { });
        }

        public override void connectAck()
        {
            lock (gate) connected = true;
            connectedEvent.Set();
        }

        public override void connectionClosed()
        {
            lock (gate)
            {
                connected = false;
                if (fatalConnectError == null)
                    fatalConnectError = "Connection closed by TWS.";
            }
            connectedEvent.Set();
            requestEvent.Set();
        }

        public override void error(Exception e)
        {
            LogError("exception: " + e.Message);
            lock (gate)
            {
                if (fatalConnectError == null)
                    fatalConnectError = e.Message;
            }
            requestEvent.Set();
        }

        public override void error(string str)
        {
            LogError(str);
        }

        public override void error(int id, long errorTime, int errorCode, string errorMsg, string advancedOrderRejectJson)
        {
            LogError($"id={id} code={errorCode}: {errorMsg}");
            if (errorCode >= 2100 && errorCode <= 2160)
                return; // informational ("market data farm is connected" etc.)
            if (errorCode == 2174)
                return; // tz-format warning; the request is still processed

            lock (gate)
            {
                if (id == -1 && FatalConnectCodes.Contains(errorCode) && fatalConnectError == null)
                    fatalConnectError = $"code {errorCode}: {errorMsg}";

                if (current != null && current.Error == null && (id == pendingReqId || id == -1 || id == 0))
                    current.Error = $"code {errorCode}: {errorMsg}";

                if (pendingTicks != null && lastTicksError == null && (id == pendingTicksReqId || id == -1 || id == 0))
                    lastTicksError = $"code {errorCode}: {errorMsg}";
            }
            requestEvent.Set();
        }

        public override void historicalData(int reqId, Bar bar)
        {
            lock (gate)
            {
                if (reqId != pendingReqId || current == null) return;
                BarRecord rec = BarRecord.FromBar(bar);
                if (rec != null)
                    current.Bars.Add(rec);
                else
                    current.DroppedBars++;
            }
        }

        public override void historicalDataUpdate(int reqId, Bar bar)
        {
            // keepUpToDate=true streams the newest bar through this callback;
            // treat it like an ordinary bar for probe purposes.
            historicalData(reqId, bar);
        }

        public override void historicalDataEnd(int reqId, string start, string end)
        {
            lock (gate)
            {
                if (reqId != pendingReqId || current == null) return;
                current.Finish(start, end);
            }
            requestEvent.Set();
        }

        // ------------------------------------------------------------ probes

        public sealed class ProbeState
        {
            public System.Collections.Generic.List<HistoricalTick> Ticks = new System.Collections.Generic.List<HistoricalTick>();
            public System.Collections.Generic.List<ContractDetails> ContractDetails = new System.Collections.Generic.List<ContractDetails>();
            public int MarketDataType;
            public double LastPrice;
            public bool HasLastPrice;
            public decimal LastSize;
            public string HeadTimestamp;
            public System.Collections.Generic.List<ContractDescription> SymbolSamples = new System.Collections.Generic.List<ContractDescription>();
            public AutoResetEvent Wake = new AutoResetEvent(false);
        }

        public ProbeState Probe { get; } = new ProbeState();

        public override void contractDetails(int reqId, ContractDetails contractDetails)
        {
            lock (gate) Probe.ContractDetails.Add(contractDetails);
            Probe.Wake.Set();
        }

        public override void contractDetailsEnd(int reqId)
        {
            Probe.Wake.Set();
        }

        public override void headTimestamp(int reqId, string headTimestamp)
        {
            lock (gate) Probe.HeadTimestamp = headTimestamp;
            Probe.Wake.Set();
        }

        public override void symbolSamples(int reqId, ContractDescription[] contractDescriptions)
        {
            lock (gate)
            {
                Probe.SymbolSamples.Clear();
                if (contractDescriptions != null)
                    Probe.SymbolSamples.AddRange(contractDescriptions);
            }
            Probe.Wake.Set();
        }

        public override void marketDataType(int reqId, int marketDataType)
        {
            lock (gate) Probe.MarketDataType = marketDataType;
            Probe.Wake.Set();
        }

        public override void tickPrice(int tickerId, int field, double price, TickAttrib attribs)
        {
            lock (gate)
            {
                Probe.LastPrice = price;
                Probe.HasLastPrice = true;
            }
        }

        public override void tickSize(int tickerId, int field, decimal size)
        {
            lock (gate) Probe.LastSize = size;
        }

        /// <summary>Snapshot of the bars collected for the current request (for keepUpToDate probes).</summary>
        public System.Collections.Generic.List<BarRecord> TakeCurrentBars()
        {
            lock (gate)
            {
                return current != null
                    ? new System.Collections.Generic.List<BarRecord>(current.Bars)
                    : new System.Collections.Generic.List<BarRecord>();
            }
        }

        // ------------------------------------------------------------ tick requests

        /// <summary>A normalized historical tick (time as unix epoch seconds).</summary>
        public struct TickPoint
        {
            public long Time;
            public double Price;
            public decimal Size;
        }

        private List<TickPoint> pendingTicks;
        private int pendingTicksReqId = -1;
        private bool pendingTicksDone;
        private string lastTicksError;

        public string LastTicksError
        {
            get { lock (gate) return lastTicksError; }
        }

        public void ResetTicksRequest(int reqId)
        {
            lock (gate)
            {
                pendingTicksReqId = reqId;
                pendingTicks = new List<TickPoint>();
                pendingTicksDone = false;
                lastTicksError = null;
            }
        }

        public override void historicalTicks(int reqId, HistoricalTick[] ticks, bool done)
        {
            lock (gate)
            {
                if (reqId != pendingTicksReqId || pendingTicks == null)
                {
                    // probe requests still collect into Probe.Ticks
                    if (ticks != null) Probe.Ticks.AddRange(ticks);
                    if (done) Probe.Wake.Set();
                    return;
                }
                if (ticks != null)
                    foreach (var t in ticks)
                        pendingTicks.Add(new TickPoint { Time = t.Time, Price = t.Price, Size = t.Size });
                if (done) pendingTicksDone = true;
            }
            requestEvent.Set();
        }

        public override void historicalTicksLast(int reqId, HistoricalTickLast[] ticks, bool done)
        {
            // server 223 answers whatToShow=TRADES with HistoricalTickLast (id 98)
            lock (gate)
            {
                if (reqId != pendingTicksReqId || pendingTicks == null) return;
                if (ticks != null)
                    foreach (var t in ticks)
                        pendingTicks.Add(new TickPoint { Time = t.Time, Price = t.Price, Size = t.Size });
                if (done) pendingTicksDone = true;
            }
            requestEvent.Set();
        }

        /// <summary>
        /// Waits for the in-flight tick request to complete (done=true) or fail.
        /// Returns null on timeout/cancellation/connection failure.
        /// </summary>
        public List<TickPoint> WaitForTicksDone(TimeSpan timeout, CancellationToken ct)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                lock (gate)
                {
                    if (pendingTicksDone && pendingTicks != null)
                    {
                        var t = pendingTicks;
                        pendingTicks = null;
                        return t;
                    }
                    if (fatalConnectError != null)
                    {
                        pendingTicks = null;
                        return null; // connection failed; LastErrorText carries the reason
                    }
                }
                if (DateTime.UtcNow >= deadline) return null;
                if (ct.IsCancellationRequested) return null;
                requestEvent.WaitOne(200);
            }
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>Prepares the wrapper to collect the response for a request.</summary>
        public void ResetRequest(int reqId)
        {
            lock (gate)
            {
                pendingReqId = reqId;
                current = new PageResult();
            }
        }

        /// <summary>
        /// Waits until the in-flight request completes (historicalDataEnd) or fails.
        /// Returns null on timeout; the result carries either bars or an error.
        /// </summary>
        public PageResult WaitForHistoricalDataEnd(TimeSpan timeout, CancellationToken ct = default)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                PageResult result = null;
                lock (gate)
                {
                    if (current != null && (current.Finished || current.Error != null))
                    {
                        result = current;
                        current = null;
                        pendingReqId = -1;
                    }
                    else if (fatalConnectError != null && current != null)
                    {
                        current.Error = current.Error ?? fatalConnectError;
                        result = current;
                        current = null;
                        pendingReqId = -1;
                    }
                }
                if (result != null) return result;
                if (DateTime.UtcNow >= deadline) return null;
                if (ct.IsCancellationRequested)
                {
                    lock (gate)
                    {
                        if (current != null) current.Cancelled = true;
                        current = null;
                        pendingReqId = -1;
                    }
                    var cancelled = new PageResult { Cancelled = true };
                    return cancelled;
                }
                requestEvent.WaitOne(200);
            }
        }

        public bool WaitUntilConnected(TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                lock (gate)
                {
                    if (connected) return true;
                    if (fatalConnectError != null) return false;
                }
                if (DateTime.UtcNow >= deadline) return false;
                connectedEvent.WaitOne(200);
            }
        }

        public string LastErrorText
        {
            get
            {
                lock (gate)
                {
                    if (fatalConnectError != null) return fatalConnectError;
                    return pendingErrors.Count > 0 ? pendingErrors[0] : "unknown error";
                }
            }
        }

        /// <summary>Routes an arbitrary error message through the configured sink (used by the reader thread).</summary>
        public void NotifyError(string message)
        {
            LogError(message);
            lock (gate)
            {
                if (fatalConnectError == null)
                    fatalConnectError = message;
            }
            requestEvent.Set();
        }

        private void LogError(string line)
        {
            lock (gate)
            {
                errorLog.AppendLine(line);
                pendingErrors.Add(line);
            }
            errorSink(line);
        }
    }
}
