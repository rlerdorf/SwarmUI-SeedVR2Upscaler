// addInstallButton(groupId, featureId, installId, buttonText)
// Place install button in "Refine / Upscale" group (groupId: refineupscale) since that's where
// the upscale method dropdown lives. The SeedVR2 Upscaler param group won't render when
// the feature is missing because all its params have FeatureFlag: "seedvr2_upscaler".
addInstallButton('refineupscale', 'seedvr2_upscaler', 'seedvr2_upscaler', 'Install SeedVR2 Upscaler Node');
