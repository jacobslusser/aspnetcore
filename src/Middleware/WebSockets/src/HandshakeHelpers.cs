// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.WebSockets
{
    internal static class HandshakeHelpers
    {
        // "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
        // This uses C# compiler's ability to refer to static data directly. For more information see https://vcsjones.dev/2019/02/01/csharp-readonly-span-bytes-static
        private static ReadOnlySpan<byte> EncodedWebSocketKey => new byte[]
        {
            (byte)'2', (byte)'5', (byte)'8', (byte)'E', (byte)'A', (byte)'F', (byte)'A', (byte)'5', (byte)'-',
            (byte)'E', (byte)'9', (byte)'1', (byte)'4', (byte)'-', (byte)'4', (byte)'7', (byte)'D', (byte)'A',
            (byte)'-', (byte)'9', (byte)'5', (byte)'C', (byte)'A', (byte)'-', (byte)'C', (byte)'5', (byte)'A',
            (byte)'B', (byte)'0', (byte)'D', (byte)'C', (byte)'8', (byte)'5', (byte)'B', (byte)'1', (byte)'1'
        };

        // Verify Method, Upgrade, Connection, version,  key, etc..
        public static void GenerateResponseHeaders(string key, string? subProtocol, IHeaderDictionary headers)
        {
            headers.Connection = HeaderNames.Upgrade;
            headers.Upgrade = Constants.Headers.UpgradeWebSocket;
            headers.SecWebSocketAccept = CreateResponseKey(key);
            if (!string.IsNullOrWhiteSpace(subProtocol))
            {
                headers.SecWebSocketProtocol = subProtocol;
            }
        }

        /// <summary>
        /// Validates the Sec-WebSocket-Key request header
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public static bool IsRequestKeyValid(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            Span<byte> temp = stackalloc byte[16];
            var success = Convert.TryFromBase64String(value, temp, out var written);
            return success && written == 16;
        }

        public static string CreateResponseKey(string requestKey)
        {
            // "The value of this header field is constructed by concatenating /key/, defined above in step 4
            // in Section 4.2.2, with the string "258EAFA5-E914-47DA-95CA-C5AB0DC85B11", taking the SHA-1 hash of
            // this concatenated value to obtain a 20-byte value and base64-encoding"
            // https://tools.ietf.org/html/rfc6455#section-4.2.2

            // requestKey is already verified to be small (24 bytes) by 'IsRequestKeyValid()' and everything is 1:1 mapping to UTF8 bytes
            // so this can be hardcoded to 60 bytes for the requestKey + static websocket string
            Span<byte> mergedBytes = stackalloc byte[60];
            Encoding.UTF8.GetBytes(requestKey, mergedBytes);
            EncodedWebSocketKey.CopyTo(mergedBytes[24..]);

            Span<byte> hashedBytes = stackalloc byte[20];
            var written = SHA1.HashData(mergedBytes, hashedBytes);
            if (written != 20)
            {
                throw new InvalidOperationException("Could not compute the hash for the 'Sec-WebSocket-Accept' header.");
            }

            return Convert.ToBase64String(hashedBytes);
        }

        // https://datatracker.ietf.org/doc/html/rfc7692#section-7.1
        public static bool ParseDeflateOptions(ReadOnlySpan<char> extension, bool serverContextTakeover,
            int serverMaxWindowBits, out WebSocketDeflateOptions parsedOptions, [NotNullWhen(true)] out string? response)
        {
            bool hasServerMaxWindowBits = false;
            bool hasClientMaxWindowBits = false;
            bool hasClientNoContext = false;
            bool hasServerNoContext = false;
            response = null;
            parsedOptions = new WebSocketDeflateOptions()
            {
                ServerContextTakeover = serverContextTakeover,
                ServerMaxWindowBits = serverMaxWindowBits
            };

            var responseLength = WebSocketDeflateConstants.Extension.Length;

            while (true)
            {
                int end = extension.IndexOf(';');
                ReadOnlySpan<char> value = (end >= 0 ? extension[..end] : extension).Trim();

                if (value.Length == 0)
                {
                    break;
                }

                if (value.SequenceEqual(WebSocketDeflateConstants.ClientNoContextTakeover))
                {
                    // https://datatracker.ietf.org/doc/html/rfc7692#section-7
                    // MUST decline if:
                    // The negotiation offer contains multiple extension parameters with
                    // the same name.
                    if (hasClientNoContext)
                    {
                        return false;
                    }

                    hasClientNoContext = true;
                    parsedOptions.ClientContextTakeover = false;
                    // 2 = '; '
                    responseLength += 2 + WebSocketDeflateConstants.ClientNoContextTakeover.Length;
                }
                else if (value.SequenceEqual(WebSocketDeflateConstants.ServerNoContextTakeover))
                {
                    // https://datatracker.ietf.org/doc/html/rfc7692#section-7
                    // MUST decline if:
                    // The negotiation offer contains multiple extension parameters with
                    // the same name.
                    if (hasServerNoContext)
                    {
                        return false;
                    }

                    hasServerNoContext = true;
                    parsedOptions.ServerContextTakeover = false;
                }
                else if (value.StartsWith(WebSocketDeflateConstants.ClientMaxWindowBits))
                {
                    // https://datatracker.ietf.org/doc/html/rfc7692#section-7
                    // MUST decline if:
                    // The negotiation offer contains multiple extension parameters with
                    // the same name.
                    if (hasClientMaxWindowBits)
                    {
                        return false;
                    }

                    hasClientMaxWindowBits = true;
                    if (!ParseWindowBits(value, WebSocketDeflateConstants.ClientMaxWindowBits, out var clientMaxWindowBits))
                    {
                        return false;
                    }

                    // 8 is a valid value according to the spec, but our zlib implementation does not support it
                    if (clientMaxWindowBits == 8)
                    {
                        return false;
                    }

                    // https://tools.ietf.org/html/rfc7692#section-7.1.2.2
                    // the server may either ignore this
                    // value or use this value to avoid allocating an unnecessarily big LZ77
                    // sliding window by including the "client_max_window_bits" extension
                    // parameter in the corresponding extension negotiation response to the
                    // offer with a value equal to or smaller than the received value.
                    parsedOptions.ClientMaxWindowBits = clientMaxWindowBits ?? 15;
                    // 2 = '; ', 1 = '='
                    responseLength += 2 + WebSocketDeflateConstants.ClientMaxWindowBits.Length + 1 +
                        ((parsedOptions.ClientMaxWindowBits > 9) ? 2 : 1);
                }
                else if (value.StartsWith(WebSocketDeflateConstants.ServerMaxWindowBits))
                {
                    // https://datatracker.ietf.org/doc/html/rfc7692#section-7
                    // MUST decline if:
                    // The negotiation offer contains multiple extension parameters with
                    // the same name.
                    if (hasServerMaxWindowBits)
                    {
                        return false;
                    }

                    hasServerMaxWindowBits = true;
                    if (!ParseWindowBits(value, WebSocketDeflateConstants.ServerMaxWindowBits, out var parsedServerMaxWindowBits))
                    {
                        return false;
                    }

                    // 8 is a valid value according to the spec, but our zlib implementation does not support it
                    if (parsedServerMaxWindowBits == 8)
                    {
                        return false;
                    }

                    // https://tools.ietf.org/html/rfc7692#section-7.1.2.1
                    // A server accepts an extension negotiation offer with this parameter
                    // by including the "server_max_window_bits" extension parameter in the
                    // extension negotiation response to send back to the client with the
                    // same or smaller value as the offer.
                    parsedOptions.ServerMaxWindowBits = Math.Min(parsedServerMaxWindowBits ?? 15, serverMaxWindowBits);
                }

                static bool ParseWindowBits(ReadOnlySpan<char> value, string propertyName, out int? parsedValue)
                {
                    var startIndex = value.IndexOf('=');

                    // parameters can be sent without a value by the client, we'll use the values set by the app developer or the default of 15
                    if (startIndex < 0)
                    {
                        parsedValue = null;
                        return true;
                    }

                    value = value[(startIndex + 1)..].TrimEnd();

                    if (value.Length == 0)
                    {
                        parsedValue = null;
                        return false;
                    }

                    // https://datatracker.ietf.org/doc/html/rfc7692#section-5.2
                    // check for value in quotes and pull the value out without the quotes
                    if (value[0] == '"' && value.EndsWith("\"".AsSpan()) && value.Length > 1)
                    {
                        value = value[1..^1];
                    }

                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int windowBits) ||
                        windowBits < 8 ||
                        windowBits > 15)
                    {
                        parsedValue = null;
                        return false;
                    }

                    parsedValue = windowBits;
                    return true;
                }

                if (end < 0)
                {
                    break;
                }
                extension = extension[(end + 1)..];
            }

            if (!parsedOptions.ServerContextTakeover)
            {
                // 2 = '; '
                responseLength += 2 + WebSocketDeflateConstants.ServerNoContextTakeover.Length;
            }

            if (hasServerMaxWindowBits || parsedOptions.ServerMaxWindowBits != 15)
            {
                // 2 = '; ', 1 = '='
                responseLength += 2 + WebSocketDeflateConstants.ServerMaxWindowBits.Length + 1 +
                    ((parsedOptions.ServerMaxWindowBits > 9) ? 2 : 1);
            }

            response = string.Create(responseLength, (parsedOptions, hasClientMaxWindowBits, hasServerMaxWindowBits, hasClientNoContext),
                static (span, state) =>
                {
                    WebSocketDeflateConstants.Extension.AsSpan().CopyTo(span);
                    span = span.Slice(WebSocketDeflateConstants.Extension.Length);
                    if (state.hasClientNoContext)
                    {
                        span[0] = ';';
                        span[1] = ' ';
                        span = span.Slice(2);
                        WebSocketDeflateConstants.ClientNoContextTakeover.AsSpan().CopyTo(span);
                        span = span.Slice(WebSocketDeflateConstants.ClientNoContextTakeover.Length);
                    }
                    if (state.hasClientMaxWindowBits)
                    {
                        // If a received extension negotiation offer doesn't have the
                        // "client_max_window_bits" extension parameter, the corresponding
                        // extension negotiation response to the offer MUST NOT include the
                        // "client_max_window_bits" extension parameter.
                        span[0] = ';';
                        span[1] = ' ';
                        span = span.Slice(2);
                        WebSocketDeflateConstants.ClientMaxWindowBits.AsSpan().CopyTo(span);
                        span = span.Slice(WebSocketDeflateConstants.ClientMaxWindowBits.Length);
                        span[0] = '=';
                        span = span.Slice(1);
                        var ret = state.parsedOptions.ClientMaxWindowBits.TryFormat(span, out var written);
                        Debug.Assert(ret);
                        span = span.Slice(written);
                    }
                    if (!state.parsedOptions.ServerContextTakeover)
                    {
                        span[0] = ';';
                        span[1] = ' ';
                        span = span.Slice(2);
                        WebSocketDeflateConstants.ServerNoContextTakeover.AsSpan().CopyTo(span);
                        span = span.Slice(WebSocketDeflateConstants.ServerNoContextTakeover.Length);
                    }
                    if (state.hasServerMaxWindowBits || state.parsedOptions.ServerMaxWindowBits != 15)
                    {
                        span[0] = ';';
                        span[1] = ' ';
                        span = span.Slice(2);
                        WebSocketDeflateConstants.ServerMaxWindowBits.AsSpan().CopyTo(span);
                        span = span.Slice(WebSocketDeflateConstants.ServerMaxWindowBits.Length);
                        span[0] = '=';
                        span = span.Slice(1);
                        var ret = state.parsedOptions.ServerMaxWindowBits.TryFormat(span, out var written);
                        Debug.Assert(ret);
                        span = span.Slice(written);
                    }
                });

            return true;
        }
    }
}
