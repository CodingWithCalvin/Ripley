using System.Globalization;
using System.Text.RegularExpressions;
namespace Reel;

public record DiscLabelHints(int? Season, int? Volume, int? DiscNumber)
{
    public static DiscLabelHints Parse(string label)
    {
        int? Read(string marker) {
            var matches=Regex.Matches(label,@"(?<![A-Z0-9])(?:"+marker+@")[ _.-]*(\d{1,3})(?=$|[^A-Z0-9])",RegexOptions.IgnoreCase);
            var values=matches.Select(m=>int.Parse(m.Groups[1].Value,CultureInfo.InvariantCulture)).Distinct().ToArray();
            // A range or multi-season collection is not a reliable single season.
            var range=Regex.IsMatch(label,@"(?<![A-Z0-9])(?:"+marker+@")[ _.]*\d{1,3}[ _.]*(?:-|&|\+|/|AND|TO)[ _.]*(?:"+marker+@")?[ _.]*\d",RegexOptions.IgnoreCase);
            return values.Length==1&&!range ? values[0] : null;
        }
        return new(Read("SEASON|S"),Read("VOLUME|VOL|V"),Read("DISC|DISK|D"));
    }
}
