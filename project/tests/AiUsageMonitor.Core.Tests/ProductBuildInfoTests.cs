using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using AiUsageMonitor.Core.Build;

namespace AiUsageMonitor.Core.Tests;

public sealed class ProductBuildInfoTests
{
    [Fact]
    public void EmbeddedVersionMatchesManagedFileAndProductMetadata()
    {
        ProductBuildInfo info = ProductBuildInfo.Current;
        Assembly assembly = typeof(ProductBuildInfo).Assembly;
        Assert.Equal("AI Usage Monitor", info.AppName);
        Assert.Matches(@"^\d+\.\d+\.\d+$", info.Version);
        Assert.Equal(info.Version, assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
        Assert.Equal(info.AppName, assembly.GetCustomAttribute<AssemblyProductAttribute>()!.Product);
        Assert.Equal(info.Version + ".0", assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version);
        Assert.Equal(info.Version, FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion);
        Assert.Equal(TimeSpan.Zero, info.BuildTimeUtc.Offset);
        Assert.True(info.BuildTimeUtc <= DateTimeOffset.UtcNow);
        Assert.True(info.SourceIdentifier == "不明" || System.Text.RegularExpressions.Regex.IsMatch(info.SourceIdentifier, "^[0-9a-f]{7,40}$"));
    }

    [Fact]
    public void DistributedJsonIsExactlyTheEmbeddedSnapshot()
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "build-info.json"));
        var distributed = System.Text.Json.JsonSerializer.Deserialize<ProductBuildInfo>(json);
        Assert.Equal(ProductBuildInfo.Current, distributed);
    }

    [Theory]
    [InlineData(true, "あり")]
    [InlineData(false, "なし")]
    [InlineData(null, "不明")]
    public void DisplayAndCopyUseSameOrderedFieldsAndUtc(bool? dirty, string expected)
    {
        var info = new ProductBuildInfo("AI Usage Monitor", "0.2.0", "不明", dirty,
            new DateTimeOffset(2026, 10, 12, 23, 0, 0, TimeSpan.FromHours(9)));
        var fields = info.GetDisplayFields();
        Assert.Equal(["アプリ名", "バージョン", "ソース識別子", "未コミット変更の有無", "ビルド日時", "実行版"], fields.Select(field => field.Key));
        Assert.Equal("不明", fields[2].Value);
        Assert.Equal(expected, fields[3].Value);
        Assert.Equal("2026-10-12 14:00:00.000 UTC", fields[4].Value);
        Assert.Equal(string.Join(Environment.NewLine, fields.Select(field => $"{field.Key}: {field.Value}")), info.ToDisplayText());
        Assert.Contains(RuntimeInformation.ProcessArchitecture.ToString(), fields[5].Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmbeddedTimestampDoesNotChangeWhenInformationIsReadAgain()
    {
        ProductBuildInfo first = ProductBuildInfo.Current;
        Assert.Same(first, ProductBuildInfo.Current);
        Assert.Equal(first.BuildTimeUtc, ProductBuildInfo.Current.BuildTimeUtc);
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            Assert.EndsWith(" UTC", first.GetDisplayFields()[4].Value, StringComparison.Ordinal);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2} ", first.GetDisplayFields()[4].Value);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
