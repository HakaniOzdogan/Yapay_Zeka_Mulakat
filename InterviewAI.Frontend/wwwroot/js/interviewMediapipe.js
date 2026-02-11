window.interviewMediapipeTracker = (() => {
    let mediaStream = null;
    let videoElement = null;
    let timerId = null;

    const state = {
        eyeContactScore: 60,
        postureScore: 60,
        confidenceScore: 60,
        attentionScore: 60,
        samples: 0
    };

    const clamp = (value, min, max) => Math.max(min, Math.min(max, value));

    function resetState() {
        state.eyeContactScore = 60;
        state.postureScore = 60;
        state.confidenceScore = 60;
        state.attentionScore = 60;
        state.samples = 0;
    }

    function updateFromFace(faceLandmarker, video) {
        const result = faceLandmarker.detectForVideo(video, performance.now());
        if (!result?.faceLandmarks?.length) {
            state.attentionScore = clamp(state.attentionScore - 1.5, 0, 100);
            state.eyeContactScore = clamp(state.eyeContactScore - 1.0, 0, 100);
            return;
        }

        const landmarks = result.faceLandmarks[0];
        const leftEye = landmarks[33];
        const rightEye = landmarks[263];
        const nose = landmarks[1];
        const eyeMidX = (leftEye.x + rightEye.x) / 2;
        const horizontalOffset = Math.abs(eyeMidX - 0.5);
        const verticalOffset = Math.abs(nose.y - 0.5);

        const eyeContact = 100 - ((horizontalOffset * 140) + (verticalOffset * 60));
        state.eyeContactScore = clamp((state.eyeContactScore * 0.8) + (eyeContact * 0.2), 0, 100);
        state.attentionScore = clamp((state.attentionScore * 0.85) + (state.eyeContactScore * 0.15), 0, 100);
    }

    function updateFromPose(poseLandmarker, video) {
        const result = poseLandmarker.detectForVideo(video, performance.now());
        if (!result?.landmarks?.length) {
            state.postureScore = clamp(state.postureScore - 1.0, 0, 100);
            state.confidenceScore = clamp(state.confidenceScore - 0.5, 0, 100);
            return;
        }

        const landmarks = result.landmarks[0];
        const leftShoulder = landmarks[11];
        const rightShoulder = landmarks[12];
        const leftHip = landmarks[23];
        const rightHip = landmarks[24];

        const shoulderTilt = Math.abs(leftShoulder.y - rightShoulder.y);
        const shoulderHipAlignment = Math.abs(((leftShoulder.x + rightShoulder.x) / 2) - ((leftHip.x + rightHip.x) / 2));

        const posture = 100 - ((shoulderTilt * 240) + (shoulderHipAlignment * 200));
        state.postureScore = clamp((state.postureScore * 0.8) + (posture * 0.2), 0, 100);
        state.confidenceScore = clamp((state.confidenceScore * 0.7) + (state.postureScore * 0.2) + (state.eyeContactScore * 0.1), 0, 100);
    }

    async function init(videoElementId) {
        await stop();
        resetState();

        videoElement = document.getElementById(videoElementId);
        if (!videoElement) {
            throw new Error("Video element not found.");
        }

        mediaStream = await navigator.mediaDevices.getUserMedia({ video: { width: 640, height: 480 }, audio: false });
        videoElement.srcObject = mediaStream;
        await videoElement.play();

        let faceLandmarker = null;
        let poseLandmarker = null;

        try {
            const vision = await import("https://cdn.jsdelivr.net/npm/@mediapipe/tasks-vision@0.10.14");
            const filesetResolver = await vision.FilesetResolver.forVisionTasks("https://cdn.jsdelivr.net/npm/@mediapipe/tasks-vision@0.10.14/wasm");

            faceLandmarker = await vision.FaceLandmarker.createFromOptions(filesetResolver, {
                baseOptions: { modelAssetPath: "https://storage.googleapis.com/mediapipe-models/face_landmarker/face_landmarker/float16/1/face_landmarker.task" },
                runningMode: "VIDEO",
                numFaces: 1
            });

            poseLandmarker = await vision.PoseLandmarker.createFromOptions(filesetResolver, {
                baseOptions: { modelAssetPath: "https://storage.googleapis.com/mediapipe-models/pose_landmarker/pose_landmarker_lite/float16/1/pose_landmarker_lite.task" },
                runningMode: "VIDEO",
                numPoses: 1
            });
        } catch {
            // MediaPipe import/model failure: keep neutral defaults.
        }

        timerId = window.setInterval(() => {
            if (!videoElement || videoElement.readyState < 2) {
                return;
            }

            if (faceLandmarker) {
                updateFromFace(faceLandmarker, videoElement);
            }
            if (poseLandmarker) {
                updateFromPose(poseLandmarker, videoElement);
            }

            if (!faceLandmarker && !poseLandmarker) {
                // Fallback for environments where CDN/model loading fails.
                state.attentionScore = clamp((state.attentionScore * 0.95) + 57 * 0.05, 0, 100);
                state.eyeContactScore = clamp((state.eyeContactScore * 0.95) + 57 * 0.05, 0, 100);
                state.postureScore = clamp((state.postureScore * 0.95) + 60 * 0.05, 0, 100);
                state.confidenceScore = clamp((state.confidenceScore * 0.95) + 58 * 0.05, 0, 100);
            }

            state.samples += 1;
        }, 500);
    }

    function getSnapshot() {
        return {
            eyeContactScore: Math.round(state.eyeContactScore),
            postureScore: Math.round(state.postureScore),
            confidenceScore: Math.round(state.confidenceScore),
            attentionScore: Math.round(state.attentionScore)
        };
    }

    async function stop() {
        if (timerId) {
            clearInterval(timerId);
            timerId = null;
        }

        if (videoElement) {
            videoElement.pause();
            videoElement.srcObject = null;
            videoElement = null;
        }

        if (mediaStream) {
            for (const track of mediaStream.getTracks()) {
                track.stop();
            }
            mediaStream = null;
        }
    }

    return { init, getSnapshot, stop };
})();
