// addInstallButton(groupId, featureId, installId, buttonText)
// Place install button in the SeedVR2 group itself.
// Group IDs are derived from group names and only keep lowercase letters:
// "SeedVR2 Upscaler" => "seedvrupscaler"
addInstallButton('seedvrupscaler', 'seedvr2_upscaler', 'seedvr2_upscaler', 'Install SeedVR2 Upscaler Node');
addInstallButton('seedvrupscaler', 'seedvr2_image_upscaler', 'seedvr2_image_upscaler', 'Install SeedVR2 Image Upscaler Node');

// Add SeedVR2 Upscale button to image/video context menus via SwarmUI's registerMediaButton API
(function() {
    let videoExtensions = ['mp4', 'webm', 'gif', 'mov', 'avi', 'mkv'];

    registerMediaButton(
        'SeedVR2 Upscale',
        (src) => {
            if (typeof currentBackendFeatureSet === 'undefined' || !currentBackendFeatureSet.includes('seedvr2_upscaler')) {
                return;
            }

            let isDataImage = src.startsWith('data:');
            let isVideo = false;
            if (!isDataImage) {
                let extension = src.split('.').pop().toLowerCase().split('?')[0];
                isVideo = videoExtensions.includes(extension);
            }

            // Data URL videos are not supported
            if (isVideo && isDataImage) {
                return;
            }

            let input_overrides = { 'images': 1 };

            if (isDataImage) {
                input_overrides['seedvr2imagefile'] = src;
            } else {
                let fullsrc = typeof getImageFullSrc === 'function' ? getImageFullSrc(src) : src;
                let prefix = typeof getImageOutPrefix === 'function' ? getImageOutPrefix() : 'Output';
                let filePath = prefix + '/' + fullsrc;
                if (isVideo) {
                    input_overrides['seedvr2videofile'] = filePath;
                } else {
                    input_overrides['seedvr2imagefile'] = filePath;
                }
            }

            // Read SeedVR2 params from UI (fixes issue #14)
            // Note: SwarmUI strips "2" from "SeedVR2" when creating element IDs
            let seedvr2Params = [
                'seedvrmodel', 'seedvrupscaleby', 'seedvrresolution', 'seedvrblockswap',
                'seedvrcolorcorrection', 'seedvrtwostepmode', 'seedvrpredownscale', 'seedvrtiledvae',
                'seedvrlatentnoisescale', 'seedvrinputnoisescale', 'seedvrcachemodel', 'seedvrattentionmode',
                'seedvrvaeoffloaddevice', 'seedvrvideobatchsize', 'seedvrtemporaloverlap', 'seedvruniformbatchsize',
                'seedvrimagetilesize', 'seedvrimagemaskblur', 'seedvrimagetileoverlap',
                'seedvrimagetileupscaleresolution', 'seedvrimagetilingstrategy', 'seedvrimageantialiasingstrength',
                'seedvrimageblendingmethod',
            ];

            for (let paramId of seedvr2Params) {
                let elem = document.getElementById('input_' + paramId);
                if (elem) {
                    // Skip toggleable params whose toggle is off (same logic as simpletab.js)
                    let toggleElem = document.getElementById('input_' + paramId + '_toggle');
                    if (toggleElem && !toggleElem.checked) {
                        continue;
                    }
                    let val = typeof getInputVal === 'function' ? getInputVal(elem, true) : elem.value;
                    if (val != null && val !== '') {
                        input_overrides[paramId] = val;
                    }
                }
            }

            if (!input_overrides['seedvrmodel']) {
                input_overrides['seedvrmodel'] = 'seedvr2-auto';
            }

            // Preserve original image metadata (fixes issue #12)
            // currentMetadataVal is a SwarmUI global holding the currently displayed image's metadata
            let metadata = typeof currentMetadataVal !== 'undefined' ? currentMetadataVal : null;
            if (metadata) {
                try {
                    let readable = typeof interpretMetadata === 'function' ? interpretMetadata(metadata) : metadata;
                    if (readable) {
                        let metadataParsed = JSON.parse(readable);
                        if (metadataParsed.sui_image_params) {
                            let skipParams = [
                                'model', 'refinermodel', 'videomodel', 'videoswapmodel', 'loras', 'loraweights', 'loratencweights',  // Models may not exist
                                'images', 'swarm_version',  // Special params
                                'width', 'height', 'aspectratio', 'sidelength'  // Resolution comes from source image
                            ];
                            for (let key in metadataParsed.sui_image_params) {
                                let keyLower = key.toLowerCase();
                                if (!keyLower.startsWith('seedvr') && !skipParams.includes(keyLower)) {
                                    input_overrides[key] = metadataParsed.sui_image_params[key];
                                }
                            }
                        }
                    }
                } catch (e) {
                    console.log('SeedVR2: Could not parse original metadata:', e);
                }
            }

            if (typeof mainGenHandler !== 'undefined' && mainGenHandler.doGenerate) {
                mainGenHandler.doGenerate(input_overrides, {});
            }
        },
        'Upscale this image/video using SeedVR2 AI upscaler',
        null,   // mediaTypes: null = images + videos
        false,  // isDefault: stays in "More" dropdown
        true    // showInHistory
    );
})();
