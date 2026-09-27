using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Nvpwr
{
    public enum DriverBackend { Kdu, EfiGuard }

    public sealed class PowerRange
    {
        public decimal OemWatts { get; private set; }
        public int MinimumWatts { get; private set; }
        public int MaximumWatts { get { return PowerProtocol.MaximumWatts; } }

        public PowerRange(decimal oemWatts)
        {
            if (oemWatts < PowerProtocol.MinimumWatts || oemWatts > PowerProtocol.MaximumWatts)
                throw new ArgumentOutOfRangeException("oemWatts", "OEM baseline is outside this application's power range.");
            OemWatts = oemWatts;
            MinimumWatts = (int)(Math.Ceiling(oemWatts / PowerProtocol.StepWatts) * PowerProtocol.StepWatts);
        }

        public bool Contains(int watts)
        {
            return PowerProtocol.IsValid(watts) && watts >= MinimumWatts;
        }

        public int[] Targets()
        {
            return Enumerable.Range(0, (MaximumWatts - MinimumWatts) / PowerProtocol.StepWatts + 1)
                .Select(index => MinimumWatts + index * PowerProtocol.StepWatts).ToArray();
        }
    }

    public sealed class PowerResult
    {
        public int Target { get; set; }
        public decimal? Watts { get; set; }
        public int ExitCode { get; set; }
        public uint? NvidiaStatus { get; set; }
        public uint? NtStatus { get; set; }
        public long DurationMilliseconds { get; set; }
        public bool Verified { get; set; }
        public bool RestartRequired { get; set; }
        public string Outcome { get; set; }
        public string Output { get; set; }
    }

    public static class PowerProtocol
    {
        public const int MinimumWatts = 5;
        public const int MaximumWatts = 300;
        public const int StepWatts = 5;

        public static decimal? ReadOemBaseline(string output)
        {
            return ReadPower(output, "OEM baseline");
        }

        private static decimal? ReadPower(string output, string label)
        {
            string text = (output ?? "").Replace("\r", "");
            string prefix = @"(?im)^[ \t]*" + Regex.Escape(label) + @"[ \t]*:";
            if (Regex.Matches(text, prefix).Count != 1) return null;
            Match value = Regex.Match(text, prefix + @"[ \t]*(\d+)[ \t]*\(([0-9]+(?:\.[0-9]+)?)[ \t]*W\)[ \t]*$");
            long raw;
            decimal shown;
            if (!value.Success || !long.TryParse(value.Groups[1].Value, out raw) || raw <= 0 ||
                !decimal.TryParse(value.Groups[2].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out shown) || shown != raw / 1000m)
                return null;
            return shown;
        }

        public static PowerRange ReadRange(string output)
        {
            string text = (output ?? "").Replace("\r", "");
            MatchCollection states = Regex.Matches(text, @"(?im)^[ \t]*State[ \t]*:[^\n]*$");
            bool stock = states.Count == 1 && Regex.IsMatch(states[0].Value, @":[ \t]*STOCK_BASELINE[ \t]*\(9\)[ \t]*$");
            bool applied = states.Count == 1 && Regex.IsMatch(states[0].Value, @":[ \t]*APPLIED[ \t]*\(2\)[ \t]*$");
            decimal? baseline = ReadOemBaseline(text);
            decimal? upper = ReadPower(text, "UPPER (+3D24)");
            if ((!stock && !applied) || ReadStatus(text, "Last NTSTATUS") != 0 || !baseline.HasValue || !upper.HasValue ||
                ReadPower(text, "MAX effective") != upper || ReadPower(text, "Current effective") != upper ||
                ReadPower(text, "Current F7") != upper || (stock && upper != baseline))
                throw new InvalidOperationException("A coherent OEM baseline could not be confirmed. Refresh the status or restart the GPU before changing power.");
            return new PowerRange(baseline.Value);
        }

        public static bool IsValid(int watts)
        {
            return watts >= MinimumWatts && watts <= MaximumWatts && watts % StepWatts == 0;
        }

        public static int[] Targets()
        {
            return Enumerable.Range(0, (MaximumWatts - MinimumWatts) / StepWatts + 1)
                .Select(index => MinimumWatts + index * StepWatts).ToArray();
        }

        public static PowerResult Parse(string output, int exitCode, int target)
        {
            if (!IsValid(target)) throw new ArgumentOutOfRangeException("target", "Expected a power target within the software bounds in 5 W steps.");
            var result = new PowerResult { Target = target, ExitCode = exitCode, Output = output ?? "", Outcome = "ReadbackUnavailable", RestartRequired = true };
            result.NvidiaStatus = ReadStatus(result.Output, "Last NVIDIA status");
            result.NtStatus = ReadStatus(result.Output, "Last NTSTATUS");
            MatchCollection matches = Regex.Matches(result.Output, @"(?im)^[ \t]*Current\s+F7\s*:\s*(\d+)\s*\(([0-9]+(?:\.[0-9]+)?)\s*W\)");
            if (matches.Count == 0) return result;
            Match last = matches[matches.Count - 1];
            long raw;
            decimal shown;
            if (!long.TryParse(last.Groups[1].Value, out raw) ||
                !decimal.TryParse(last.Groups[2].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out shown) || shown != raw / 1000m)
                return result;
            result.Watts = shown;
            result.RestartRequired = raw != target * 1000L;
            if (result.RestartRequired) { result.Outcome = "Mismatch"; return result; }
            bool stock = IsStockBaseline(result.Output, target, exitCode);
            bool successfulStatuses = (!result.NvidiaStatus.HasValue || result.NvidiaStatus.Value == 0) && (!result.NtStatus.HasValue || result.NtStatus.Value == 0);
            result.Verified = (exitCode == 0 && successfulStatuses) || stock;
            bool nvidiaTimeout = exitCode != 0 && result.NvidiaStatus == 0x65 && result.NtStatus == 0;
            result.Outcome = stock ? "StockBaseline" : result.Verified ? "Applied" : nvidiaTimeout ? "NvidiaTimeout" : "ControllerFailure";
            return result;
        }

        private static uint? ReadStatus(string output, string label)
        {
            if (Regex.Matches(output, @"(?im)^[ \t]*" + Regex.Escape(label) + @"[ \t]*:").Count != 1) return null;
            MatchCollection matches = Regex.Matches(output.Replace("\r", ""), @"(?im)^[ \t]*" + Regex.Escape(label) + @"[ \t]*:[ \t]*0x([0-9a-f]{1,8})[ \t]*$");
            uint value;
            return matches.Count == 1 && uint.TryParse(matches[0].Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value) ? (uint?)value : null;
        }

        private static bool IsStockBaseline(string output, int watts, int exitCode)
        {
            if (exitCode != 0 && exitCode != 6) return false;
            string text = output.Replace("\r", "");
            if (exitCode == 6 && !Regex.IsMatch(text, @"(?im)^[ \t]*Set failed:[ \t]*Win32=23[ \t]*$")) return false;
            string[] required = {
                @"(?im)^[ \t]*State[ \t]*:[ \t]*STOCK_BASELINE[ \t]*\(9\)[ \t]*$",
                @"(?im)^[ \t]*Detail[ \t]*:[ \t]*0[ \t]*$",
                @"(?im)^[ \t]*Last NTSTATUS[ \t]*:[ \t]*0x0+[ \t]*$",
                @"(?im)^[ \t]*Last NVIDIA status[ \t]*:[ \t]*0x0+[ \t]*$",
                @"(?im)^[ \t]*Flags init/elig/amt[ \t]*:[ \t]*1[ \t]*/[ \t]*0[ \t]*/[ \t]*0[ \t]*$"
            };
            if (required.Any(pattern => Regex.Matches(text, pattern).Count != 1)) return false;
            string[] fields = { "OEM baseline", "F7 input (+3D14)", "UPPER (+3D24)", "MAX effective", "Current effective", "Current F7", "Applied target", "amount (+3D18)" };
            foreach (string field in fields)
            {
                MatchCollection values = Regex.Matches(text, @"(?im)^[ \t]*" + Regex.Escape(field) + @"[ \t]*:[ \t]*(\d+)[ \t]*\(([0-9]+(?:\.[0-9]+)?)[ \t]*W\)[ \t]*$");
                if (values.Count != 1) return false;
                long raw;
                decimal shown;
                int expected = field == "amount (+3D18)" ? 0 : watts;
                if (!long.TryParse(values[0].Groups[1].Value, out raw) ||
                    !decimal.TryParse(values[0].Groups[2].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out shown) ||
                    raw != expected * 1000L || shown != expected) return false;
            }
            return true;
        }
    }
}