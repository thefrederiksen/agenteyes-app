// Three engine helpers are Unix-generic even though they live under Platform/Linux: running a
// short-lived tool and reading both its streams, writing a streaming WAV header, and creating a
// credential file that only its owner can read. None of them touches X, PulseAudio or v4l2, so the
// macOS build compiles those three files as they are rather than duplicating them - see
// UnixSharedCompile in AgentEyes.Core.csproj.
//
// They are aliased into the macOS compilation here so that macOS code can use them by their plain
// names and NOT read as if it were Linux code. This file is compiled only on macOS; the Linux build
// never sees it and is unchanged.
global using CredentialFile = AgentEyes.Platform.Linux.CredentialFile;
global using ToolResult = AgentEyes.Platform.Linux.ToolResult;
global using ToolRunner = AgentEyes.Platform.Linux.ToolRunner;
global using WavStreamWriter = AgentEyes.Platform.Linux.WavStreamWriter;
