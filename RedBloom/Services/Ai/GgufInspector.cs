using System.IO;
using System.Text;

namespace RedBloom.Services.Ai;

/// <summary>
/// What a diffusion GGUF turns out to be, read from the file rather than guessed from its name.
/// </summary>
/// <param name="Architecture">The <c>general.architecture</c> metadata value, when the file carries one.</param>
/// <param name="HasVae">Whether the file contains the image decoder (VAE) tensors.</param>
/// <param name="HasTextEncoder">Whether it contains a text encoder (CLIP) at all.</param>
/// <param name="HasTwoTextEncoders">Whether it carries two — the mark of an SDXL checkpoint.</param>
public sealed record GgufInfo(
    string? Architecture, bool HasVae, bool HasTextEncoder, bool HasTwoTextEncoders);

/// <summary>
/// Reads the header of a GGUF file — its metadata and the names of its tensors — without touching
/// the weights.
/// </summary>
/// <remarks>
/// The whole point is to tell, for certain, what an image model needs to run: whether it is an
/// SDXL checkpoint, and whether it already contains a VAE and text encoders or expects them to be
/// supplied alongside. All of that is decided by which tensors are present, and the tensor list
/// sits at the very start of the file, ahead of the gigabytes of weights — so this reads a few
/// kilobytes and stops. A file it cannot make sense of yields null rather than a guess, and the
/// caller falls back to reading the name.
/// </remarks>
public static class GgufInspector
{
    private const uint Magic = 0x46554747; // "GGUF", little-endian.

    public static GgufInfo? Inspect(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

            if (reader.ReadUInt32() != Magic)
            {
                return null;
            }

            var version = reader.ReadUInt32();

            // Version 1 laid the counts out as 32-bit; 2 and 3 use 64-bit, which is everything in
            // circulation. Rather than support a format no current tool writes, an old file is left
            // to the name-based fallback.
            if (version is not (2 or 3))
            {
                return null;
            }

            var tensorCount = reader.ReadUInt64();
            var kvCount = reader.ReadUInt64();

            string? architecture = null;

            for (ulong i = 0; i < kvCount; i++)
            {
                var key = ReadString(reader);
                var type = reader.ReadUInt32();
                var value = ReadValue(reader, type);

                if (key == "general.architecture" && value is string text)
                {
                    architecture = text;
                }
            }

            var hasVae = false;
            var hasClipL = false;
            var hasClipG = false;

            // A model can carry a great many tensors; the cap only guards against a corrupt count
            // sending this into a loop it cannot leave.
            var cap = (long)Math.Min(tensorCount, 1_000_000);

            for (long i = 0; i < cap; i++)
            {
                var name = ReadString(reader).ToLowerInvariant();

                var dimensions = reader.ReadUInt32();
                for (uint d = 0; d < dimensions; d++)
                {
                    reader.ReadUInt64();
                }

                reader.ReadUInt32(); // ggml type
                reader.ReadUInt64(); // offset into the tensor data

                if (name.Contains("first_stage_model", StringComparison.Ordinal)
                    || name.Contains(".vae.", StringComparison.Ordinal)
                    || name.StartsWith("vae.", StringComparison.Ordinal))
                {
                    hasVae = true;
                }

                // SDXL carries two text encoders (CLIP-L and the larger CLIP-G); SD1.5 carries one.
                // The names differ between checkpoint layouts, so several spellings are checked.
                if (name.Contains("embedders.1", StringComparison.Ordinal)
                    || name.Contains("clip_g", StringComparison.Ordinal)
                    || name.Contains("text_model_2", StringComparison.Ordinal)
                    || name.Contains("text_encoders.clip_g", StringComparison.Ordinal))
                {
                    hasClipG = true;
                }

                if (name.Contains("embedders.0", StringComparison.Ordinal)
                    || name.Contains("clip_l", StringComparison.Ordinal)
                    || name.Contains("cond_stage_model", StringComparison.Ordinal)
                    || name.Contains("text_encoders.clip_l", StringComparison.Ordinal)
                    || name.Contains("conditioner.embedders", StringComparison.Ordinal))
                {
                    hasClipL = true;
                }
            }

            return new GgufInfo(architecture, hasVae, hasClipL || hasClipG, hasClipL && hasClipG);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException
                                       or UnauthorizedAccessException or OverflowException)
        {
            return null;
        }
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadUInt64();

        // A sane cap: a metadata string or tensor name is short. A length past this means the file
        // is not laid out as expected, and reading it as a string would consume the whole file.
        if (length > 1024 * 1024)
        {
            throw new EndOfStreamException("Implausible string length in GGUF header.");
        }

        return Encoding.UTF8.GetString(reader.ReadBytes((int)length));
    }

    /// <summary>
    /// Reads a metadata value, returning it only when it is a string and otherwise consuming its
    /// bytes so the reader lands on the next key. An unknown type cannot be sized, so it stops the
    /// whole read.
    /// </summary>
    private static object? ReadValue(BinaryReader reader, uint type)
    {
        switch (type)
        {
            case 0 or 1 or 7: reader.ReadByte(); return null;              // uint8 / int8 / bool
            case 2 or 3: reader.ReadUInt16(); return null;                 // uint16 / int16
            case 4 or 5 or 6: reader.ReadUInt32(); return null;            // uint32 / int32 / float32
            case 10 or 11 or 12: reader.ReadUInt64(); return null;         // uint64 / int64 / float64
            case 8: return ReadString(reader);                            // string
            case 9:                                                       // array
                var elementType = reader.ReadUInt32();
                var count = reader.ReadUInt64();

                for (ulong i = 0; i < count; i++)
                {
                    ReadValue(reader, elementType);
                }

                return null;
            default:
                throw new EndOfStreamException($"Unknown GGUF value type {type}.");
        }
    }
}
