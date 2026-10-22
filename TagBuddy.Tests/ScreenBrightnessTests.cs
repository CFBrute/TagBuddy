using TagBuddy.Services;

namespace TagBuddy.Tests;

/// <summary>Where the full-screen code view starts its brightness slider.</summary>
public class ScreenBrightnessTests
{
    [Theory]
    [InlineData(0f, 0.2f)]
    [InlineData(0.05f, 0.2f)]
    [InlineData(0.2f, 0.4f)]
    [InlineData(0.4f, 0.8f)]
    [InlineData(0.5f, 1f)]
    [InlineData(1f, 1f)]
    public void StartLevel_doubles_the_phone_brightness_between_20_and_100_percent(float current, float expected) =>
        Assert.Equal(expected, ScreenBrightness.StartLevel(current, alwaysMax: false), 3);

    [Theory]
    [InlineData(0f)]
    [InlineData(0.3f)]
    [InlineData(1f)]
    public void StartLevel_is_full_when_always_max_is_on(float current) =>
        Assert.Equal(1f, ScreenBrightness.StartLevel(current, alwaysMax: true));
}
