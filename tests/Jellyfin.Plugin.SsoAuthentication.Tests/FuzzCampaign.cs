using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.SsoAuthentication.Tests;

/// <summary>Bounded, seeded mutations with the offending input saved on failure.</summary>
internal sealed class FuzzCampaign(ITestOutputHelper output)
{
    private readonly int _seed = Setting("FUZZ_SEED", 7331, 0, int.MaxValue);
    private readonly int _iterations = Setting("FUZZ_ITERATIONS", 64, 1, 100_000);

    public async Task Run(string target, Func<Random, int, string> generate, Func<string, Task> check, string? certificate = null)
    {
        output.WriteLine($"{target}: FUZZ_SEED={_seed} FUZZ_ITERATIONS={_iterations}");
        var random = new Random(_seed);
        for (var iteration = 0; iteration < _iterations; iteration++)
        {
            var input = generate(random, iteration);
            try
            {
                await check(input);
            }
            catch (Exception error)
            {
                var directory = Environment.GetEnvironmentVariable("FUZZ_ARTIFACTS") ?? "artifacts/fuzz";
                Directory.CreateDirectory(directory);
                var path = Path.GetFullPath(Path.Combine(directory, target + ".json"));
                File.WriteAllText(path, JsonSerializer.Serialize(new
                {
                    Target = target,
                    Seed = _seed,
                    Iteration = iteration,
                    Iterations = _iterations,
                    InputBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(input)),
                    CertificateBase64 = certificate,
                    Error = error.ToString(),
                }, new JsonSerializerOptions { WriteIndented = true }));
                output.WriteLine($"Failure at iteration {iteration}; input saved to {path}");
                throw;
            }
        }
    }

    private static int Setting(string name, int fallback, int minimum, int maximum)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (value is null)
        {
            return fallback;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) || result < minimum || result > maximum)
        {
            throw new ArgumentException($"{name} must be an integer from {minimum} to {maximum}.");
        }

        return result;
    }
}

internal static class FuzzInputs
{
    // Keep known attacks before generated mutations so even a short run exercises them.
    public static readonly string[] Corpus =
    [
        "", "=", "====", "AA==", "not-base64!", "\0", "\r\nforged: header", "'\"\\",
        "</script><script>alert(1)</script>", "../admin", "%00%2f%2e", "state=a&state=b",
        "admin\u202e", "用户", "😀", "<", "<Response/>",
        "<!DOCTYPE r [<!ENTITY x 'expanded'>]><r>&x;</r>",
        "<!DOCTYPE r [<!ENTITY x SYSTEM 'file:///sso-fuzz-nonexistent'>]><r>&x;</r>",
        "<r xmlns:ds='http://www.w3.org/2000/09/xmldsig#'><ds:Signature/></r>",
        new string('A', 16_384),
    ];

    public static string Text(Random random, int iteration)
        => iteration < Corpus.Length ? Corpus[iteration] : Mutate(random, Corpus[random.Next(Corpus.Length)]);

    public static string Mutate(Random random, string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input).ToList();
        var mutations = random.Next(1, 9);
        for (var mutation = 0; mutation < mutations; mutation++)
        {
            var position = random.Next(bytes.Count + 1);
            switch (random.Next(4))
            {
                case 0 when position < bytes.Count:
                    bytes[position] ^= (byte)(1 << random.Next(8));
                    break;
                case 1 when position < bytes.Count:
                    bytes.RemoveRange(position, random.Next(1, Math.Min(64, bytes.Count - position) + 1));
                    break;
                case 2:
                    bytes.InsertRange(position, Encoding.UTF8.GetBytes(Corpus[random.Next(Corpus.Length)]));
                    break;
                default:
                    bytes.Insert(position, (byte)random.Next(256));
                    break;
            }

            // Bound individual inputs, including insertions of the long corpus member.
            if (bytes.Count > 32_768)
            {
                bytes.RemoveRange(32_768, bytes.Count - 32_768);
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    public static string XmlText(Random random)
        => "fuzz-" + new string(Text(random, random.Next(Corpus.Length + 128)).Where(System.Xml.XmlConvert.IsXmlChar).Take(128).ToArray());
}
