using System.Security.Cryptography;

namespace ReStore.Core.src.utils;

public class EncryptionMetadata
{
    public byte[] Salt { get; set; } = Array.Empty<byte>();
    public byte[] IV { get; set; } = Array.Empty<byte>();
    public byte[] EncryptedDEK { get; set; } = Array.Empty<byte>();
    public string Algorithm { get; set; } = "AES-256-GCM";
    public int Version { get; set; } = 1;
    public int KeyDerivationIterations { get; set; } = 1_000_000;
}

public class EncryptionService
{
    private const int KEY_SIZE_BYTES = 32;
    private const int SALT_SIZE_BYTES = 32;
    private const int IV_SIZE_BYTES = 12;
    private const int TAG_SIZE_BYTES = 16;
    private const int DEFAULT_ITERATIONS = 1_000_000;

    private readonly ILogger _logger;

    public EncryptionService(ILogger logger)
    {
        _logger = logger;
    }

    public byte[] DeriveKeyFromPassword(string password, byte[] salt, int iterations = DEFAULT_ITERATIONS)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256
        );
        return pbkdf2.GetBytes(KEY_SIZE_BYTES);
    }

    public async Task<EncryptionMetadata> EncryptFileAsync(string inputPath, string outputPath, string password, byte[]? salt = null)
    {
        _logger.Log($"Encrypting file: {Path.GetFileName(inputPath)}", LogLevel.Debug);

        salt ??= RandomNumberGenerator.GetBytes(SALT_SIZE_BYTES);
        
        var kek = DeriveKeyFromPassword(password, salt);
        var dek = RandomNumberGenerator.GetBytes(KEY_SIZE_BYTES);
        var iv = RandomNumberGenerator.GetBytes(IV_SIZE_BYTES);

        var encryptedDEK = EncryptDEK(dek, kek);

        await EncryptFileWithDEKAsync(inputPath, outputPath, dek, iv);

        var metadata = new EncryptionMetadata
        {
            Salt = salt,
            IV = iv,
            EncryptedDEK = encryptedDEK,
            Algorithm = "AES-256-GCM",
            Version = 1,
            KeyDerivationIterations = DEFAULT_ITERATIONS
        };

        _logger.Log($"File encrypted successfully: {Path.GetFileName(outputPath)}", LogLevel.Info);
        return metadata;
    }

    public async Task DecryptFileAsync(string inputPath, string outputPath, string password, EncryptionMetadata metadata)
    {
        _logger.Log($"Decrypting file: {Path.GetFileName(inputPath)}", LogLevel.Debug);

        var kek = DeriveKeyFromPassword(password, metadata.Salt, metadata.KeyDerivationIterations);
        var dek = DecryptDEK(metadata.EncryptedDEK, kek);

        await DecryptFileWithDEKAsync(inputPath, outputPath, dek, metadata.IV);

        _logger.Log($"File decrypted successfully: {Path.GetFileName(outputPath)}", LogLevel.Info);
    }

    private const int CHUNK_SIZE = 1024 * 1024; // 1MB

    private async Task EncryptFileWithDEKAsync(string inputPath, string outputPath, byte[] key, byte[] iv)
    {
        await Task.Run(() =>
        {
            using var aesGcm = new AesGcm(key, TAG_SIZE_BYTES);
            using var inputStream = File.OpenRead(inputPath);
            using var outputStream = File.Create(outputPath);

            // Write initial IV (base IV)
            outputStream.Write(iv, 0, iv.Length);

            var buffer = new byte[CHUNK_SIZE];
            var chunkIV = new byte[IV_SIZE_BYTES];
            var tag = new byte[TAG_SIZE_BYTES];
            int bytesRead;
            long chunkIndex = 0;

            while ((bytesRead = inputStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                // Derive IV for this chunk: BaseIV + ChunkIndex
                Array.Copy(iv, chunkIV, IV_SIZE_BYTES);
                IncrementIV(chunkIV, chunkIndex);

                var ciphertext = new byte[bytesRead];
                var plaintextSpan = buffer.AsSpan(0, bytesRead);
                
                aesGcm.Encrypt(chunkIV, plaintextSpan, ciphertext, tag);

                // Write Chunk Length (4 bytes)
                outputStream.Write(BitConverter.GetBytes(bytesRead));
                // Write Tag (16 bytes)
                outputStream.Write(tag);
                // Write Ciphertext
                outputStream.Write(ciphertext);

                chunkIndex++;
            }
        });
    }

    private async Task DecryptFileWithDEKAsync(string inputPath, string outputPath, byte[] key, byte[] iv)
    {
        await Task.Run(() =>
        {
            using var aesGcm = new AesGcm(key, TAG_SIZE_BYTES);
            using var inputStream = File.OpenRead(inputPath);
            using var outputStream = File.Create(outputPath);

            var storedIV = new byte[IV_SIZE_BYTES];
            if (inputStream.Read(storedIV, 0, IV_SIZE_BYTES) != IV_SIZE_BYTES)
            {
                throw new InvalidOperationException("Invalid encrypted file format (missing IV)");
            }

            var lengthBuffer = new byte[4];
            var tag = new byte[TAG_SIZE_BYTES];
            var chunkIV = new byte[IV_SIZE_BYTES];
            long chunkIndex = 0;

            while (inputStream.Read(lengthBuffer, 0, 4) == 4)
            {
                int chunkLength = BitConverter.ToInt32(lengthBuffer, 0);
                if (chunkLength < 0 || chunkLength > CHUNK_SIZE)
                {
                        throw new InvalidOperationException($"Invalid chunk length: {chunkLength}");
                }

                if (inputStream.Read(tag, 0, TAG_SIZE_BYTES) != TAG_SIZE_BYTES)
                {
                    throw new InvalidOperationException("Unexpected EOF reading tag");
                }

                var ciphertext = new byte[chunkLength];
                if (inputStream.Read(ciphertext, 0, chunkLength) != chunkLength)
                {
                    throw new InvalidOperationException("Unexpected EOF reading ciphertext");
                }

                Array.Copy(storedIV, chunkIV, IV_SIZE_BYTES);
                IncrementIV(chunkIV, chunkIndex);

                var plaintext = new byte[chunkLength];
                try
                {
                    aesGcm.Decrypt(chunkIV, ciphertext, tag, plaintext);
                    outputStream.Write(plaintext, 0, chunkLength);
                }
                catch (CryptographicException ex)
                {
                    throw new InvalidOperationException($"Decryption failed at chunk {chunkIndex}.", ex);
                }

                chunkIndex++;
            }
        });
    }

    private void IncrementIV(byte[] iv, long counter)
    {
        // Increment the last 8 bytes (64 bits) of the 96-bit IV
        // This treats the last part of the IV as a counter for chunk uniqueness
        
        ulong currentLow = 0;
        for (int i = 0; i < 8; i++)
        {
            currentLow = (currentLow << 8) | iv[IV_SIZE_BYTES - 8 + i];
        }
        
        currentLow += (ulong)counter;
        
        for (int i = 0; i < 8; i++)
        {
            iv[IV_SIZE_BYTES - 1 - i] = (byte)(currentLow & 0xFF);
            currentLow >>= 8;
        }
    }

    private byte[] EncryptDEK(byte[] dek, byte[] kek)
    {
        using var aesGcm = new AesGcm(kek, TAG_SIZE_BYTES);
        var iv = RandomNumberGenerator.GetBytes(IV_SIZE_BYTES);
        var encryptedDEK = new byte[dek.Length];
        var tag = new byte[TAG_SIZE_BYTES];

        aesGcm.Encrypt(iv, dek, encryptedDEK, tag);

        var result = new byte[IV_SIZE_BYTES + TAG_SIZE_BYTES + encryptedDEK.Length];
        Buffer.BlockCopy(iv, 0, result, 0, IV_SIZE_BYTES);
        Buffer.BlockCopy(tag, 0, result, IV_SIZE_BYTES, TAG_SIZE_BYTES);
        Buffer.BlockCopy(encryptedDEK, 0, result, IV_SIZE_BYTES + TAG_SIZE_BYTES, encryptedDEK.Length);

        return result;
    }

    private byte[] DecryptDEK(byte[] encryptedData, byte[] kek)
    {
        using var aesGcm = new AesGcm(kek, TAG_SIZE_BYTES);
        
        var iv = new byte[IV_SIZE_BYTES];
        var tag = new byte[TAG_SIZE_BYTES];
        var encryptedDEK = new byte[encryptedData.Length - IV_SIZE_BYTES - TAG_SIZE_BYTES];

        Buffer.BlockCopy(encryptedData, 0, iv, 0, IV_SIZE_BYTES);
        Buffer.BlockCopy(encryptedData, IV_SIZE_BYTES, tag, 0, TAG_SIZE_BYTES);
        Buffer.BlockCopy(encryptedData, IV_SIZE_BYTES + TAG_SIZE_BYTES, encryptedDEK, 0, encryptedDEK.Length);

        var dek = new byte[encryptedDEK.Length];
        
        try
        {
            aesGcm.Decrypt(iv, encryptedDEK, tag, dek);
            return dek;
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("Failed to decrypt DEK. Invalid password.", ex);
        }
    }

    public async Task SaveMetadataAsync(EncryptionMetadata metadata, string metadataPath)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(metadata, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
        await File.WriteAllTextAsync(metadataPath, json);
    }

    public async Task<EncryptionMetadata> LoadMetadataAsync(string metadataPath)
    {
        if (!File.Exists(metadataPath))
        {
            throw new FileNotFoundException($"Encryption metadata not found: {metadataPath}");
        }

        var json = await File.ReadAllTextAsync(metadataPath);
        var metadata = System.Text.Json.JsonSerializer.Deserialize<EncryptionMetadata>(json);
        
        if (metadata == null)
        {
            throw new InvalidOperationException("Failed to parse encryption metadata");
        }
        return metadata;
    }

    public static byte[] GenerateSalt()
    {
        return RandomNumberGenerator.GetBytes(SALT_SIZE_BYTES);
    }

    public bool ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return false;
        }

        if (password.Length < 8)
        {
            _logger.Log("Password must be at least 8 characters long", LogLevel.Warning);
            return false;
        }

        return true;
    }

    public string CreatePasswordVerificationToken(string password, byte[] salt, int iterations = DEFAULT_ITERATIONS)
    {
        const string VERIFICATION_TEXT = "ReStore_Password_Verification_Token";
        
        var kek = DeriveKeyFromPassword(password, salt, iterations);
        var iv = RandomNumberGenerator.GetBytes(IV_SIZE_BYTES);
        
        using var aesGcm = new AesGcm(kek, TAG_SIZE_BYTES);
        var plaintext = System.Text.Encoding.UTF8.GetBytes(VERIFICATION_TEXT);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TAG_SIZE_BYTES];
        
        aesGcm.Encrypt(iv, plaintext, ciphertext, tag);
        
        var combined = new byte[iv.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(iv, 0, combined, 0, iv.Length);
        Buffer.BlockCopy(tag, 0, combined, iv.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, combined, iv.Length + tag.Length, ciphertext.Length);
        
        return Convert.ToBase64String(combined);
    }

    public bool VerifyPassword(string password, byte[] salt, string verificationToken, int iterations = DEFAULT_ITERATIONS)
    {
        try
        {
            const string VERIFICATION_TEXT = "ReStore_Password_Verification_Token";
            
            var kek = DeriveKeyFromPassword(password, salt, iterations);
            var combined = Convert.FromBase64String(verificationToken);
            
            var iv = new byte[IV_SIZE_BYTES];
            var tag = new byte[TAG_SIZE_BYTES];
            var ciphertext = new byte[combined.Length - IV_SIZE_BYTES - TAG_SIZE_BYTES];
            
            Buffer.BlockCopy(combined, 0, iv, 0, IV_SIZE_BYTES);
            Buffer.BlockCopy(combined, IV_SIZE_BYTES, tag, 0, TAG_SIZE_BYTES);
            Buffer.BlockCopy(combined, IV_SIZE_BYTES + TAG_SIZE_BYTES, ciphertext, 0, ciphertext.Length);
            
            using var aesGcm = new AesGcm(kek, TAG_SIZE_BYTES);
            var plaintext = new byte[ciphertext.Length];
            
            aesGcm.Decrypt(iv, ciphertext, tag, plaintext);
            var decryptedText = System.Text.Encoding.UTF8.GetString(plaintext);
            
            return decryptedText == VERIFICATION_TEXT;
        }
        catch (CryptographicException)
        {
            _logger.Log("Password verification failed: incorrect password", LogLevel.Warning);
            return false;
        }
        catch (Exception ex)
        {
            _logger.Log($"Password verification error: {ex.Message}", LogLevel.Error);
            return false;
        }
    }
}
