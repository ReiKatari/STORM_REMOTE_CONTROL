// =============================================================================
// CryptoEngine.cs
//
// AES-256-GCM encryption engine with ECDH P-256 key exchange for
// STORM REMOTE CONTROL secure communications.
// =============================================================================

using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Threading;

namespace StormRemoteControl.Core
{
    /// <summary>
    /// Provides AES-256-GCM authenticated encryption with ECDH P-256 key exchange.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each instance generates an ephemeral ECDH key pair on construction.
    /// After exchanging public keys with a peer, call <see cref="DeriveSessionKey"/>
    /// to establish the shared AES-256 session key.
    /// </para>
    /// <para>
    /// Encrypted packet format: <c>[nonce(12)][tag(16)][ciphertext(N)]</c>.
    /// Nonce is counter-based: 4-byte random prefix (set at construction) followed by
    /// an 8-byte monotonically incrementing counter (thread-safe).
    /// </para>
    /// </remarks>
    public sealed class CryptoEngine : IDisposable
    {
        /// <summary>The size of the AES-256 key in bytes.</summary>
        private const int KeySizeBytes = 32;

        /// <summary>The size of the AES-GCM nonce in bytes.</summary>
        private const int NonceSizeBytes = 12;

        /// <summary>The size of the AES-GCM authentication tag in bytes.</summary>
        private const int TagSizeBytes = 16;

        /// <summary>Total overhead added to plaintext: nonce + tag.</summary>
        private const int OverheadBytes = NonceSizeBytes + TagSizeBytes;

        /// <summary>Offset of the random prefix within the nonce.</summary>
        private const int NoncePrefixSize = 4;

        /// <summary>Size of the counter portion within the nonce.</summary>
        private const int NonceCounterSize = 8;

        private readonly ECDiffieHellman _ecdh;
        private readonly byte[] _noncePrefix;

        private AesGcm? _aesGcm;
        private byte[]? _sessionKey;
        private long _nonceCounter;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of <see cref="CryptoEngine"/>,
        /// generating a fresh ECDH P-256 key pair and a random 4-byte nonce prefix.
        /// </summary>
        public CryptoEngine()
        {
            _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

            _noncePrefix = new byte[NoncePrefixSize];
            RandomNumberGenerator.Fill(_noncePrefix);
        }

        /// <summary>
        /// Gets a value indicating whether a session key has been derived and
        /// the engine is ready to encrypt/decrypt.
        /// </summary>
        public bool IsSessionEstablished => _aesGcm is not null;

        /// <summary>
        /// Gets the local ECDH public key bytes for transmission to the peer.
        /// </summary>
        /// <returns>
        /// The exported ECDH public key in X.509 SubjectPublicKeyInfo format.
        /// </returns>
        /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
        public byte[] GetPublicKey()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        }

        /// <summary>
        /// Derives a shared AES-256 session key from the peer's ECDH public key.
        /// </summary>
        /// <param name="peerPublicKey">
        /// The peer's public key in X.509 SubjectPublicKeyInfo format,
        /// as returned by <see cref="GetPublicKey"/> on the remote side.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="peerPublicKey"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="peerPublicKey"/> is empty or invalid.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// A session key has already been derived.
        /// </exception>
        /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
        public void DeriveSessionKey(byte[] peerPublicKey)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(peerPublicKey);

            if (peerPublicKey.Length == 0)
            {
                throw new ArgumentException("Peer public key must not be empty.", nameof(peerPublicKey));
            }

            if (_aesGcm is not null)
            {
                throw new InvalidOperationException("Session key has already been derived.");
            }

            using ECDiffieHellman peerEcdh = ECDiffieHellman.Create();
            peerEcdh.ImportSubjectPublicKeyInfo(peerPublicKey, out _);

            // Derive raw shared secret via ECDH
            byte[] sharedSecret = _ecdh.DeriveKeyMaterial(peerEcdh.PublicKey);

            try
            {
                // Hash the shared secret with SHA-256 to produce a uniform 32-byte AES key
                _sessionKey = SHA256.HashData(sharedSecret);
                _aesGcm = new AesGcm(_sessionKey, TagSizeBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(sharedSecret);
            }
        }

        /// <summary>
        /// Encrypts the specified plaintext using AES-256-GCM.
        /// </summary>
        /// <param name="plaintext">The data to encrypt.</param>
        /// <returns>
        /// A byte array in the format <c>[nonce(12)][tag(16)][ciphertext(N)]</c>.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// No session key has been derived. Call <see cref="DeriveSessionKey"/> first.
        /// </exception>
        /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
        public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureSessionEstablished();

            byte[] output = new byte[OverheadBytes + plaintext.Length];
            EncryptInPlace(plaintext, output);
            return output;
        }

        /// <summary>
        /// Decrypts an encrypted packet in the format <c>[nonce(12)][tag(16)][ciphertext(N)]</c>.
        /// </summary>
        /// <param name="encryptedPacket">The encrypted packet to decrypt.</param>
        /// <returns>The decrypted plaintext bytes.</returns>
        /// <exception cref="ArgumentException">
        /// <paramref name="encryptedPacket"/> is shorter than the minimum overhead.
        /// </exception>
        /// <exception cref="CryptographicException">
        /// Decryption failed; the packet has been tampered with or the key is incorrect.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// No session key has been derived. Call <see cref="DeriveSessionKey"/> first.
        /// </exception>
        /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
        public byte[] Decrypt(ReadOnlySpan<byte> encryptedPacket)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureSessionEstablished();

            if (encryptedPacket.Length < OverheadBytes)
            {
                throw new ArgumentException(
                    $"Encrypted packet must be at least {OverheadBytes} bytes (nonce + tag).",
                    nameof(encryptedPacket));
            }

            int ciphertextLength = encryptedPacket.Length - OverheadBytes;
            byte[] output = new byte[ciphertextLength];
            DecryptInPlace(encryptedPacket, output);
            return output;
        }

        /// <summary>
        /// Encrypts plaintext into the provided output span with zero heap allocation.
        /// </summary>
        /// <param name="plaintext">The data to encrypt.</param>
        /// <param name="output">
        /// The output buffer. Must be at least <c>plaintext.Length + 28</c> bytes.
        /// Layout: <c>[nonce(12)][tag(16)][ciphertext(N)]</c>.
        /// </param>
        /// <returns>The total number of bytes written to <paramref name="output"/>.</returns>
        /// <exception cref="ArgumentException">
        /// <paramref name="output"/> is too small.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// No session key has been derived. Call <see cref="DeriveSessionKey"/> first.
        /// </exception>
        /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
        public int EncryptInPlace(ReadOnlySpan<byte> plaintext, Span<byte> output)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureSessionEstablished();

            int requiredSize = OverheadBytes + plaintext.Length;
            if (output.Length < requiredSize)
            {
                throw new ArgumentException(
                    $"Output buffer must be at least {requiredSize} bytes " +
                    $"(plaintext {plaintext.Length} + overhead {OverheadBytes}).",
                    nameof(output));
            }

            // Build nonce: [4-byte random prefix][8-byte counter]
            Span<byte> nonce = output[..NonceSizeBytes];
            _noncePrefix.CopyTo(nonce);
            long counter = Interlocked.Increment(ref _nonceCounter);
            BinaryPrimitives.WriteInt64BigEndian(nonce[NoncePrefixSize..], counter);

            // Partition output for tag and ciphertext
            Span<byte> tag = output.Slice(NonceSizeBytes, TagSizeBytes);
            Span<byte> ciphertext = output.Slice(OverheadBytes, plaintext.Length);

            _aesGcm!.Encrypt(nonce, plaintext, ciphertext, tag);

            return requiredSize;
        }

        /// <summary>
        /// Decrypts an encrypted packet in-place into the provided output span.
        /// </summary>
        /// <param name="encryptedPacket">
        /// The encrypted packet in format <c>[nonce(12)][tag(16)][ciphertext(N)]</c>.
        /// </param>
        /// <param name="output">
        /// The output buffer for the decrypted plaintext. Must be at least
        /// <c>encryptedPacket.Length - 28</c> bytes.
        /// </param>
        /// <returns>The number of plaintext bytes written to <paramref name="output"/>.</returns>
        /// <exception cref="ArgumentException">
        /// <paramref name="encryptedPacket"/> is too short or <paramref name="output"/> is too small.
        /// </exception>
        /// <exception cref="CryptographicException">
        /// Decryption failed; the packet has been tampered with or the key is incorrect.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// No session key has been derived. Call <see cref="DeriveSessionKey"/> first.
        /// </exception>
        /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
        public int DecryptInPlace(ReadOnlySpan<byte> encryptedPacket, Span<byte> output)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureSessionEstablished();

            if (encryptedPacket.Length < OverheadBytes)
            {
                throw new ArgumentException(
                    $"Encrypted packet must be at least {OverheadBytes} bytes (nonce + tag).",
                    nameof(encryptedPacket));
            }

            int ciphertextLength = encryptedPacket.Length - OverheadBytes;
            if (output.Length < ciphertextLength)
            {
                throw new ArgumentException(
                    $"Output buffer must be at least {ciphertextLength} bytes.",
                    nameof(output));
            }

            ReadOnlySpan<byte> nonce = encryptedPacket[..NonceSizeBytes];
            ReadOnlySpan<byte> tag = encryptedPacket.Slice(NonceSizeBytes, TagSizeBytes);
            ReadOnlySpan<byte> ciphertext = encryptedPacket[OverheadBytes..];

            _aesGcm!.Decrypt(nonce, ciphertext, tag, output[..ciphertextLength]);

            return ciphertextLength;
        }

        /// <summary>
        /// Releases all cryptographic resources and zeroes the session key material.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _aesGcm?.Dispose();
            _ecdh.Dispose();

            if (_sessionKey is not null)
            {
                CryptographicOperations.ZeroMemory(_sessionKey);
                _sessionKey = null;
            }
        }

        /// <summary>
        /// Throws <see cref="InvalidOperationException"/> if no session key has been derived.
        /// </summary>
        private void EnsureSessionEstablished()
        {
            if (_aesGcm is null)
            {
                throw new InvalidOperationException(
                    "Session key has not been derived. Call DeriveSessionKey first.");
            }
        }
    }
}
