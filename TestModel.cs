using System;
using System.Collections.Generic;

namespace Cs2Ts;

/// <summary>
/// Demonstrates a rich model for TypeScript generation.
/// </summary>
[ConvertToTs]
public class TestModel
{
    /// <summary>
    /// Display name for the model.
    /// </summary>
    public string? Name { get; set; }
    public int Age { get; set; }
    public DateTime? BirthDate { get; set; }
    public bool? IsActive { get; set; }

    // Date/time and duration formats (type-only metadata; serialization is handled elsewhere)
    public DateTimeOffset? CreatedAt { get; set; }
    public TimeSpan? ProcessingTime { get; set; }
    public List<TimeSpan>? RetryDelays { get; set; }
    public DateTimeOffset[]? History { get; set; }
    
    // Test Action and Func types
    public Action? SimpleAction { get; set; }
    public Action<string>? ActionWithParam { get; set; }
    public Action<string, int>? ActionWithTwoParams { get; set; }
    public Func<string>? FuncWithReturn { get; set; }
    public Func<int, string>? FuncWithParamAndReturn { get; set; }
    public Func<string, int, bool>? FuncWithTwoParamsAndReturn { get; set; }
    
    // Test new attributes
    [ReactNode]
    public object? ReactContent { get; set; }
    
    [HtmlElement]
    public object? DomElement { get; set; }

    // Test type-only imports (should generate `import type`)
    public OtherModel? Other { get; set; }

    // Test enum literal type emission (should generate `typeof UiColor.Black`)
    public UiColor DefaultColorLiteral => UiColor.Black;
    
    // Test nullable function types
    public Action? NullableAction { get; set; }
    public Func<string>? NullableFunc { get; set; }

    // Test value usage of enum (should generate a non-type-only import)
    public static readonly UiColor DefaultColor = UiColor.Black;
}

/// <summary>
/// Represents a percussive sound that can be used in beat generation.
/// </summary>
[ConvertToTs]
public class BeatSound
{
    /// <summary>
    /// Human readable name of the sound.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Identifier used to locate the sound asset.
    /// </summary>
    public required string SoundId { get; set; }
    
    /// <summary>
    /// Default metronome sound.
    /// </summary>
    public static readonly BeatSound Default = new BeatSound
    {
        Name = "Default",
        SoundId = "Default"
    };

    /// <summary>
    /// Click sound that accentuates beats.
    /// </summary>
    public static readonly BeatSound Click = new BeatSound
    {
        Name = "Click",
        SoundId = "Click"
    };
}

/// <summary>
/// A marker interface for actions that can contain other actions. We don't have a GetChildren() method, because we're using C# to Ts conversion.
/// </summary>
[ConvertToTs]
public interface ICompositeAction
{
}
