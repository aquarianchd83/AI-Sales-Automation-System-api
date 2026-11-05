using System.Text.Json;
using WhatsAppSalesAutomation.Application.ContentStudio.Cinematic;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Cinematic;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

public class CinematicScriptTests
{
    private static CinematicScript Good() => new(
        "Guru Ji ka nyay", "A couple, a quarrel, a laugh.", "Why is dinner cold?", "A husband and wife argue; Guru Ji settles it.", "Hinglish", "Ab garam chai pi lo!",
        new[] { new CinematicCharacter("Ramesh", "Middle-aged man", "Loud"), new CinematicCharacter("Sunita", "Wife", "Sharp"), new CinematicCharacter("Guru Ji", "Elderly sage", "Calm") },
        new[]
        {
            Scene(1, new CinematicDialogue("Ramesh", "Khana thanda kyun hai?", "angry", "Khana thanda kyun hai?", 500, 3000)),
            Scene(2, new CinematicDialogue("Sunita", "Tum der se aaye!", "defensive", "Tum der se aaye!", 500, 3000),
                     new CinematicDialogue("Guru Ji", "Shaant ho jao.", "calm", "Shaant ho jao.", 3500, 6000)),
            Scene(3, new CinematicDialogue("Guru Ji", "Dono ek doosre ko chai pilao.", "smiling", "Dono ek doosre ko chai pilao.", 1000, 5000))
        });

    private static CinematicScene Scene(int index, params CinematicDialogue[] lines) =>
        new(index, 10, "Kitchen", "Medium shot", "Cut", "Utensils", "Same clothes", $"Scene {index} prompt", lines);

    private static string Serialize(CinematicScript s) => JsonSerializer.Serialize(s);

    [Fact]
    public void A_complete_script_is_valid()
    {
        var result = CinematicScriptValidator.Validate(Serialize(Good()), 30);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.NotNull(result.Script);
    }

    [Fact]
    public void Malformed_json_is_rejected_without_throwing()
    {
        var result = CinematicScriptValidator.Validate("{ not json", 30);
        Assert.False(result.IsValid);
        Assert.Null(result.Script);
    }

    [Fact]
    public void A_speaker_who_is_not_a_declared_character_is_rejected()
    {
        var script = Good();
        var bad = script with { Scenes = new[] { script.Scenes[0] with { Dialogue = new[] { new CinematicDialogue("Stranger", "Hi", "x", "Hi", 0, 1000) } }, script.Scenes[1], script.Scenes[2] } };
        var result = CinematicScriptValidator.Validate(Serialize(bad), 30);
        Assert.Contains(result.Errors, e => e.Contains("Stranger"));
    }

    [Fact]
    public void Overlapping_dialogue_is_rejected()
    {
        var script = Good();
        var overlap = script.Scenes[1] with
        {
            Dialogue = new[] { new CinematicDialogue("Sunita", "a", "x", "a", 0, 3000), new CinematicDialogue("Guru Ji", "b", "x", "b", 2000, 4000) }
        };
        var result = CinematicScriptValidator.Validate(Serialize(script with { Scenes = new[] { script.Scenes[0], overlap, script.Scenes[2] } }), 30);
        Assert.Contains(result.Errors, e => e.Contains("overlaps"));
    }

    [Fact]
    public void Dialogue_running_past_the_scene_is_rejected()
    {
        var script = Good();
        var long1 = script.Scenes[0] with { Dialogue = new[] { new CinematicDialogue("Ramesh", "a", "x", "a", 8000, 11000) } };
        var result = CinematicScriptValidator.Validate(Serialize(script with { Scenes = new[] { long1, script.Scenes[1], script.Scenes[2] } }), 30);
        Assert.Contains(result.Errors, e => e.Contains("past the end"));
    }

    [Fact]
    public void Total_duration_far_from_target_is_rejected()
    {
        var script = Good();
        var result = CinematicScriptValidator.Validate(Serialize(script with { Scenes = new[] { script.Scenes[0] } }), 30);
        Assert.Contains(result.Errors, e => e.Contains("target is 30"));
    }

    [Fact]
    public void The_hash_changes_with_the_words_but_not_with_camera_notes()
    {
        var script = Good();
        var camera = script with { Scenes = script.Scenes.Select(s => s with { Camera = "Wide shot" }).ToArray() };
        var words = script with { Scenes = new[] { script.Scenes[0] with { Dialogue = new[] { new CinematicDialogue("Ramesh", "Different", "x", "Different", 500, 3000) } }, script.Scenes[1], script.Scenes[2] } };

        Assert.Equal(CinematicScriptValidator.ContentHash(script), CinematicScriptValidator.ContentHash(camera));
        Assert.NotEqual(CinematicScriptValidator.ContentHash(script), CinematicScriptValidator.ContentHash(words));
    }

    [Fact]
    public void A_vertical_1080p_mp4_with_audio_near_target_passes()
        => Assert.True(FinalVideoRules.Check(new VideoProbe(1080, 1920, 30, 30.4, true, "mov,mp4,m4a,3gp,3g2,mj2"), 30).Passed);

    [Fact]
    public void Landscape_silent_or_wrong_length_video_fails_with_each_reason()
    {
        var result = FinalVideoRules.Check(new VideoProbe(1920, 1080, 12, 12, false, "matroska,webm"), 30);
        Assert.False(result.Passed);
        Assert.Equal(5, result.Problems.Count);
    }

    [Fact]
    public async Task The_factory_returns_the_requested_provider_and_flow_import_never_makes_a_clip()
    {
        var flow = new FlowImportSceneVideoProvider();
        var factory = new SceneVideoProviderFactory(new ISceneVideoProvider[] { flow });
        Assert.Same(flow, factory.For(SceneVideoProviderKind.FlowImport));
        Assert.Throws<InvalidOperationException>(() => factory.For(SceneVideoProviderKind.Veo));

        var result = await flow.SubmitAsync(new SceneClipRequest(Guid.NewGuid(), "p", 8, "9:16", Array.Empty<string>(), "key"));
        Assert.Equal(SceneClipState.AwaitingImport, result.State);
        Assert.Null(result.ClipPath);
    }
}
