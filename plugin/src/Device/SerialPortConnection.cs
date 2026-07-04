// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception

using System;
using System.IO.Ports;

namespace Uniflag.Device
{
    /// <summary>
    /// Production <see cref="ISerialConnection"/> over
    /// <see cref="SerialPort"/>. The transport is USB CDC-ACM: baud is
    /// ignored (native USB), and DTR is asserted because that is how a host
    /// signals "terminal open" — some CDC stacks gate their TX on it.
    /// </summary>
    public sealed class SerialPortConnection : ISerialConnection
    {
        /// <summary>
        /// Read poll bound: <see cref="Read"/> returns 0 after this long
        /// with no data, so the RX pump can observe stop/fault flags at
        /// least this often.
        /// </summary>
        public const int ReadTimeoutMs = 100;

        /// <summary>
        /// Write progress bound. A device that stops accepting ~3 KB bulk
        /// writes for this long is yanked or wedged — the write faults
        /// (TimeoutException) so the TX pump can unwind into a reconnect
        /// instead of hanging teardown.
        /// </summary>
        public const int WriteTimeoutMs = 2000;

        private readonly SerialPort _port;

        /// <summary>Open <paramref name="portName"/>; throws on failure.</summary>
        public SerialPortConnection(string portName)
        {
            _port = new SerialPort(portName)
            {
                ReadTimeout = ReadTimeoutMs,
                WriteTimeout = WriteTimeoutMs,
                DtrEnable = true,
            };
            _port.Open();
        }

        /// <inheritdoc />
        public string PortName => _port.PortName;

        /// <inheritdoc />
        public int Read(byte[] buffer, int offset, int count)
        {
            try
            {
                // SerialPort.Read returns as soon as >= 1 byte is available.
                return _port.Read(buffer, offset, count);
            }
            catch (TimeoutException)
            {
                return 0; // quiet link, not a dead one — map to the poll contract
            }
        }

        /// <inheritdoc />
        public void Write(byte[] buffer, int offset, int count)
        {
            _port.Write(buffer, offset, count);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            try
            {
                _port.Dispose();
            }
            catch
            {
                // Already-yanked hardware can throw from the close path;
                // there is nothing further to release.
            }
        }
    }

    /// <summary>Production <see cref="ISerialConnectionFactory"/>.</summary>
    public sealed class SerialPortConnectionFactory : ISerialConnectionFactory
    {
        /// <inheritdoc />
        public ISerialConnection Open(string portName)
        {
            return new SerialPortConnection(portName);
        }
    }
}
