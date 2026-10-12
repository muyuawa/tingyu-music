# Muyu Music integration

Upstream: https://github.com/luckyelysia/LiquidGlassWinUI

Managed sources are pinned to commit `647fd60` (refraction direction and spectral dispersion fixes). The native x64 runtime comes from the pinned `LiquidGlassWinUI` 1.0.3 NuGet package, whose source commit is `77d9ff9d99a7c388c67e07d8c15755e79618aec5`. Windows App SDK is pinned to 2.2.0. The audio engine is retained.

Muyu changes: expose per-instance shader connection/error status; use the host's solid fallback instead of a red error fill; clear failed graph handles to avoid disposing them twice; document these additions. Runtime module SHA256 pins in glass-runtime.json disable advanced glass when the shipped module set changes. This checks compatibility, not a security signature.

The native library patches composition modules inside the Muyu process. It does not modify system DLL files or inject into other applications. Private ABI compatibility remains a limitation; Windows 10 needs its own runtime validation. The user explicitly authorized this route after reviewing the public-API limitation.

Upstream MIT license is retained in LICENSE. The original author and upstream references remain in the source. This is not an Apple-native implementation or a claim of physical optical accuracy.
