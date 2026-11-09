namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>Who can see a story character. Private characters never leave their tenant; sharing is an explicit act.</summary>
public enum StoryCharacterVisibility
{
    Private = 0,
    Shared = 1
}

/// <summary>How a character came to exist. Only <see cref="ScriptGenerated"/> costs money to create.</summary>
public enum StoryCharacterOrigin
{
    Uploaded = 0,
    Library = 1,
    ScriptGenerated = 2
}

/// <summary>A reference image's life. Approved images are never overwritten: a revision becomes a new row and the old one is Superseded.</summary>
public enum StoryReferenceStatus
{
    Uploaded = 0,
    Generated = 1,
    Approved = 2,
    Superseded = 3,
    Rejected = 4
}

public enum StoryProjectStatus
{
    Draft = 0,
    ScriptApproved = 1,
    Generating = 2,
    InReview = 3,
    Approved = 4,
    Failed = 5,
    Cancelled = 6
}

/// <summary>Where a scene's clip comes from. Switchable per project: Flow is a manual import because Flow has no public API.</summary>
public enum SceneVideoProviderKind
{
    Simulator = 0,
    Veo = 1,
    FlowImport = 2
}

public enum StoryScriptStatus
{
    Draft = 0,
    Approved = 1,
    Superseded = 2
}

public enum StorySceneStatus
{
    Pending = 0,
    Queued = 1,
    Processing = 2,
    /// <summary>Waiting for the user to generate the clip in Flow and upload it.</summary>
    AwaitingImport = 3,
    Completed = 4,
    Failed = 5,
    Flagged = 6
}

public enum StoryMediaKind
{
    CharacterImage = 0,
    SceneClip = 1,
    DialogueAudio = 2,
    Subtitles = 3,
    FinalVideo = 4,
    Intermediate = 5
}

/// <summary>Where a finished story was shared. Recorded by hand: the studio does not publish for the user.</summary>
public enum StoryPlatform
{
    YouTubeShorts = 0,
    FacebookReels = 1,
    InstagramReels = 2
}

public enum StoryJobKind
{
    Script = 0,
    CharacterImage = 1,
    SceneClip = 2,
    Speech = 3,
    LipSync = 4,
    Render = 5
}

public enum StoryJobStatus
{
    Queued = 0,
    Processing = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4
}

/// <summary>Where a tenant's request for a video stands: raised, the platform admin has written the script, the video is attached.
/// Sent as its name (the portal compares to words).</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum StoryVideoRequestStatus
{
    Raised = 0,
    ScriptReady = 1,
    VideoReady = 2
}
