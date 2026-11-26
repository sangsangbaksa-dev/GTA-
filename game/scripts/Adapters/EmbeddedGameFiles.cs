using Harbor.Core.Ports;

namespace Harbor.Game.Adapters;

internal sealed class EmbeddedGameFiles() : EmbeddedFileSystem(typeof(EmbeddedGameFiles).Assembly);
