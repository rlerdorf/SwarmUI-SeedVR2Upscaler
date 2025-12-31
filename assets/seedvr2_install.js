// addInstallButton(groupId, featureId, installId, buttonText)
// Place install button in the SeedVR2 group itself.
// Group IDs are derived from group names and only keep lowercase letters:
// "SeedVR2 Upscaler" => "seedvrupscaler"
addInstallButton('seedvrupscaler', 'seedvr2_upscaler', 'seedvr2_upscaler', 'Install SeedVR2 Upscaler Node');

// Add SeedVR2 Upscale button to image/video context menus
(function() {
    // Video file extensions supported by SeedVR2
    let videoExtensions = ['mp4', 'webm', 'gif', 'mov', 'avi', 'mkv'];

    // Wait for buttonsForImage to be defined, then wrap it
    let checkInterval = setInterval(() => {
        if (typeof buttonsForImage === 'function') {
            clearInterval(checkInterval);

            // Store original function
            let originalButtonsForImage = buttonsForImage;

            // Replace with wrapped version
            buttonsForImage = function(fullsrc, src, metadata) {
                // Call original function
                let buttons = originalButtonsForImage(fullsrc, src, metadata);

                // Only add SeedVR2 button if feature is available
                if (typeof currentBackendFeatureSet !== 'undefined' &&
                    currentBackendFeatureSet.includes('seedvr2_upscaler')) {

                    // Skip data URLs
                    let isDataImage = src.startsWith('data:');
                    if (!isDataImage) {
                        // Determine if this is a video or image based on extension
                        let extension = src.split('.').pop().toLowerCase().split('?')[0];
                        let isVideo = videoExtensions.includes(extension);

                        buttons.push({
                            label: 'SeedVR2 Upscale',
                            title: 'Upscale this ' + (isVideo ? 'video' : 'image') + ' using SeedVR2 AI upscaler',
                            onclick: (e) => {
                                // Use getImageOutPrefix() to get correct prefix (Output or View/{user_id})
                                let prefix = typeof getImageOutPrefix === 'function' ? getImageOutPrefix() : 'Output';
                                let filePath = prefix + '/' + fullsrc;

                                // Build input overrides with the appropriate file parameter
                                let input_overrides = {
                                    'images': 1
                                };

                                if (isVideo) {
                                    input_overrides['seedvr2videofile'] = filePath;
                                } else {
                                    input_overrides['seedvr2imagefile'] = filePath;
                                }

                                // Trigger generation with the file parameter
                                if (typeof mainGenHandler !== 'undefined' && mainGenHandler.doGenerate) {
                                    mainGenHandler.doGenerate(input_overrides, {});
                                }
                            }
                        });
                    }
                }

                return buttons;
            };
        }
    }, 100);
})();
