// Serial transport for the LX200 command set spoken by the DIY mount firmware.
//
// Every command is sent as ":<cmd>#". The firmware answers in one of three ways:
//   - nothing at all              -> SendBlind
//   - a single character, no '#'  -> SendChar     (e.g. :Sr#, :Te#, :MS# success)
//   - text terminated by '#'      -> SendString   (e.g. :GR#, :GW#)
//
// All transactions are serialised with a lock because the ASCOM local server can call the
// driver from several threads at once.

using System;
using System.Diagnostics;
using System.IO.Ports;
using System.Text;

namespace ASCOM.DIYAstroMount.Telescope
{
    internal sealed class Lx200Port : IDisposable
    {
        private const int ReplyTimeoutMs = 2000;
        private readonly object sync = new object();
        private readonly Action<string, string> log;
        private SerialPort port;

        internal Lx200Port(Action<string, string> logger)
        {
            log = logger;
        }

        internal bool IsOpen => port != null && port.IsOpen;

        /// <summary>
        /// Opens the port and checks that a mount answers the LX200 ACK handshake.
        /// </summary>
        internal void Open(string portName, int baudRate)
        {
            lock (sync)
            {
                Close();

                port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
                {
                    Handshake = Handshake.None,
                    // The ESP32 auto-reset circuit pulls EN low through DTR/RTS; keep both inactive
                    // so opening the port does not reboot the mount and lose its position.
                    DtrEnable = false,
                    RtsEnable = false,
                    Encoding = Encoding.ASCII,
                    ReadTimeout = ReplyTimeoutMs,
                    WriteTimeout = ReplyTimeoutMs,
                    NewLine = "#",
                };
                port.Open();

                // If the board did reset anyway it needs a moment to boot (and prints boot-ROM noise),
                // so retry the handshake for a few seconds before giving up.
                var timer = Stopwatch.StartNew();
                Exception lastError = null;
                while (timer.ElapsedMilliseconds < 8000)
                {
                    try
                    {
                        port.DiscardInBuffer();
                        port.Write(new byte[] { 0x06 }, 0, 1);
                        int reply = ReadByte(500);
                        log("Lx200Port", $"ACK => {(char)reply}");
                        if (reply == 'G' || reply == 'P' || reply == 'A')
                        {
                            System.Threading.Thread.Sleep(50);
                            port.DiscardInBuffer();
                            return;
                        }
                    }
                    catch (TimeoutException ex)
                    {
                        lastError = ex;
                    }
                    System.Threading.Thread.Sleep(250);
                }

                Close();
                throw new NotConnectedException($"No LX200 mount responded on {portName}.", lastError);
            }
        }

        internal void Close()
        {
            lock (sync)
            {
                if (port == null) return;
                try { if (port.IsOpen) port.Close(); } catch { }
                try { port.Dispose(); } catch { }
                port = null;
            }
        }

        public void Dispose() => Close();

        /// <summary>Sends a command that has no reply.</summary>
        internal void SendBlind(string command)
        {
            lock (sync)
            {
                Write(command);
                log("LX200", $"{Frame(command)} => (no reply)");
            }
        }

        /// <summary>Sends a command whose reply is a single character with no terminator.</summary>
        internal char SendChar(string command)
        {
            lock (sync)
            {
                Write(command);
                char reply = (char)ReadByte(ReplyTimeoutMs);
                log("LX200", $"{Frame(command)} => {reply}");
                return reply;
            }
        }

        /// <summary>Sends a command whose reply is terminated by '#'. The '#' is removed.</summary>
        internal string SendString(string command)
        {
            lock (sync)
            {
                Write(command);
                string reply = ReadToHash(ReplyTimeoutMs);
                log("LX200", $"{Frame(command)} => {reply}#");
                return reply;
            }
        }

        /// <summary>
        /// Sends a command whose reply is either a single failure character or a status character
        /// followed by '#'-terminated text. Used for :MS# ("0" or "1msg#"/"2msg#") and
        /// :SC# ("0" or "1Updating Planetary Data#<spaces>#").
        /// </summary>
        /// <param name="command">The command.</param>
        /// <param name="textFollows">Returns true if text follows the given first character.</param>
        /// <param name="textBlocks">Number of '#'-terminated blocks that follow when text is present.</param>
        /// <param name="text">The first text block (without the status character).</param>
        internal char SendCharWithText(string command, Func<char, bool> textFollows, int textBlocks, out string text)
        {
            lock (sync)
            {
                Write(command);
                char first = (char)ReadByte(ReplyTimeoutMs);
                text = string.Empty;
                if (textFollows(first))
                {
                    text = ReadToHash(ReplyTimeoutMs);
                    for (int i = 1; i < textBlocks; i++) ReadToHash(ReplyTimeoutMs);
                }
                log("LX200", $"{Frame(command)} => {first}{text}");
                return first;
            }
        }

        /// <summary>Raw pass-through for CommandBlind/Bool/String with raw = true.</summary>
        internal void WriteRaw(string text)
        {
            lock (sync)
            {
                EnsureOpen();
                port.DiscardInBuffer();
                port.Write(text);
                log("LX200", $"raw {text}");
            }
        }

        internal char ReadRawChar()
        {
            lock (sync) { return (char)ReadByte(ReplyTimeoutMs); }
        }

        internal string ReadRawToHash()
        {
            lock (sync) { return ReadToHash(ReplyTimeoutMs); }
        }

        private static string Frame(string command) => ":" + command + "#";

        private void EnsureOpen()
        {
            if (!IsOpen) throw new NotConnectedException("The serial port is not open.");
        }

        private void Write(string command)
        {
            EnsureOpen();
            port.DiscardInBuffer(); // drop anything stale so replies stay in step with commands
            port.Write(Frame(command));
        }

        private int ReadByte(int timeoutMs)
        {
            EnsureOpen();
            port.ReadTimeout = timeoutMs;
            int b = port.ReadByte(); // throws TimeoutException
            if (b < 0) throw new TimeoutException();
            return b;
        }

        private string ReadToHash(int timeoutMs)
        {
            var sb = new StringBuilder();
            var timer = Stopwatch.StartNew();
            while (true)
            {
                int remaining = timeoutMs - (int)timer.ElapsedMilliseconds;
                if (remaining <= 0) throw new TimeoutException($"Timed out waiting for '#' after \"{sb}\".");
                int b = ReadByte(remaining);
                if (b == '#') return sb.ToString();
                sb.Append((char)b);
                if (sb.Length > 200) throw new DriverException($"Over-long reply from mount: \"{sb}\".");
            }
        }
    }
}
