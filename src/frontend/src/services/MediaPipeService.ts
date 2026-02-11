/**
 * MediaPipe Landmarker service wrapper
 * Loads and manages Face Landmarker + Pose Landmarker models
 */
import { FaceLandmarker, PoseLandmarker, FilesetResolver } from '@mediapipe/tasks-vision';

let faceLandmarker: FaceLandmarker | null = null;
let poseLandmarker: PoseLandmarker | null = null;
let lastTimestampMs = 0;

export async function initMediaPipe() {
  const vision = await FilesetResolver.forVisionTasks(
    'https://cdn.jsdelivr.net/npm/@mediapipe/tasks-vision@0.10.0/wasm'
  );

  const initWithDelegate = async (delegate: 'GPU' | 'CPU') => {
    faceLandmarker = await FaceLandmarker.createFromOptions(vision, {
      baseOptions: {
        delegate,
        modelAssetPath: '/models/face_landmarker.task'
      },
      runningMode: 'VIDEO',
      numFaces: 1
    });

    poseLandmarker = await PoseLandmarker.createFromOptions(vision, {
      baseOptions: {
        delegate,
        modelAssetPath: '/models/pose_landmarker_lite.task'
      },
      runningMode: 'VIDEO',
      numPoses: 1,
      minPoseDetectionConfidence: 0.3,
      minPosePresenceConfidence: 0.3,
      minTrackingConfidence: 0.3
    });
  };

  try {
    await initWithDelegate('GPU');
  } catch (gpuError) {
    console.warn('MediaPipe GPU init failed, retrying with CPU...', gpuError);
    await initWithDelegate('CPU');
  }
}

export interface LandmarkFrame {
  face: any | null;
  pose: any | null;
  timestamp: number;
}

export async function detectLandmarks(
  video: HTMLVideoElement,
  timestamp: number
): Promise<LandmarkFrame> {
  if (!faceLandmarker || !poseLandmarker) {
    throw new Error('MediaPipe not initialized');
  }

  // MediaPipe VIDEO mode expects monotonically increasing timestamps.
  const safeTimestamp = Math.max(timestamp, lastTimestampMs + 1);
  lastTimestampMs = safeTimestamp;

  let faceResult = null;
  let poseResult = null;

  try {
    faceResult = faceLandmarker.detectForVideo(video, safeTimestamp);
  } catch (e) {
    console.warn('Face detection error:', e);
  }

  try {
    poseResult = poseLandmarker.detectForVideo(video, safeTimestamp);
  } catch (e) {
    console.warn('Pose detection error:', e);
  }

  return {
    face: faceResult,
    pose: poseResult,
    timestamp: safeTimestamp
  };
}

export function isMediaPipeReady(): boolean {
  return faceLandmarker !== null && poseLandmarker !== null;
}
