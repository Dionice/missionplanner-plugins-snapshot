using System;
using System.IO.Ports;
using System.Threading;
using System.Diagnostics;

namespace CompanionPlugin
{
    public static class Storm32Serial
    {
        /// <summary>
        /// Send a raw binary packet to the STorM32 device over the given COM port.
        /// </summary>
        public static bool SendRawPacket(string portName, int baudRate, byte[] packet, int readTimeoutMs = 200)
        {
            try
            {
                using (var sp = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One))
                {
                    sp.ReadTimeout = readTimeoutMs;
                    sp.WriteTimeout = 500;
                    sp.Open();
                    sp.DiscardInBuffer();
                    sp.DiscardOutBuffer();
                    sp.Write(packet, 0, packet.Length);
                    // small delay to allow device to respond
                    Thread.Sleep(50);

                    try
                    {
                        int avail = sp.BytesToRead;
                        if (avail > 0)
                        {
                            var buf = new byte[avail];
                            sp.Read(buf, 0, avail);
                            Trace.WriteLine("Storm32Serial: recv " + BitConverter.ToString(buf));
                        }
                    }
                    catch (TimeoutException) { }

                    sp.Close();
                }

                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("Storm32Serial.SendRawPacket error: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Send a human-readable ASCII console command (followed by newline) to the device.
        /// Useful when the firmware supports console commands.
        /// </summary>
        public static bool SendAsciiCommand(string portName, int baudRate, string cmd, int readTimeoutMs = 500)
        {
            try
            {
                using (var sp = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One))
                {
                    sp.NewLine = "\n";
                    sp.ReadTimeout = readTimeoutMs;
                    sp.WriteTimeout = 500;
                    sp.Open();
                    sp.DiscardInBuffer();
                    sp.DiscardOutBuffer();
                    sp.Write(cmd);
                    sp.Write(sp.NewLine);
                    Thread.Sleep(50);

                    try
                    {
                        string resp = sp.ReadExisting();
                        if (!string.IsNullOrEmpty(resp)) Trace.WriteLine("Storm32Serial resp: " + resp.Trim());
                    }
                    catch (TimeoutException) { }

                    sp.Close();
                }

                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("Storm32Serial.SendAsciiCommand error: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Attempt to set pan mode via an ASCII console command. Many STorM32 firmwares accept
        /// simple text commands; try this first and fall back to a raw packet if needed.
        /// </summary>
        public static bool SetPanModeAscii(string portName, int baudRate, int mode)
        {
            // Common first attempt: "mode <n>". If your firmware uses a different console command,
            // replace the string below with the appropriate command (see STorM32 docs).
            string cmd = $"mode {mode}";
            return SendAsciiCommand(portName, baudRate, cmd);
        }

        /// <summary>
        /// Send a raw STorM32 packet that sets pan mode. The caller must build the packet
        /// using the checksum/format described in the STorM32 Serial Communication docs.
        /// </summary>
        public static bool SetPanModeRaw(string portName, int baudRate, byte[] packet)
        {
            return SendRawPacket(portName, baudRate, packet);
        }
    }
}
