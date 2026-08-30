using System.Runtime.CompilerServices;

#if !TIESKY_SIGNED_DEPLOYMENT
[assembly: InternalsVisibleTo("Tiesky.Image2D.Tests")]
[assembly: InternalsVisibleTo("Tiesky.Image2D.Benchmarks")]
#endif
