import React, { useEffect, useRef } from 'react';

interface LandmarkPoint {
  x: number;
  y: number;
  z?: number;
  visibility?: number;
}

interface VideoCanvasProps {
  width: number;
  height: number;
  stream: MediaStream | null;
  onFrame: (video: HTMLVideoElement, canvas: HTMLCanvasElement) => void;
  drawLandmarks?: boolean;
  faceLandmarks?: LandmarkPoint[];
  poseLandmarks?: LandmarkPoint[];
  showFaceDetails?: boolean;
  showPoseDetails?: boolean;
  showDiagnostics?: boolean;
}

interface FaceHudInfo {
  mouthLabel: string;
  mouthRatio: number;
  yaw: number;
  pitch: number;
  roll: number;
}

interface PoseHudInfo {
  postureLabel: string;
  shoulderTilt: number;
  torsoTilt: number;
  needsFraming: boolean;
}

export const VideoCanvas: React.FC<VideoCanvasProps> = ({
  width,
  height,
  stream,
  onFrame,
  drawLandmarks = false,
  faceLandmarks,
  poseLandmarks,
  showFaceDetails = true,
  showPoseDetails = true,
  showDiagnostics = true
}) => {
  const videoRef = useRef<HTMLVideoElement>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const animationRef = useRef<number | null>(null);
  const onFrameRef = useRef(onFrame);
  const drawLandmarksRef = useRef(drawLandmarks);
  const faceLandmarksRef = useRef(faceLandmarks);
  const poseLandmarksRef = useRef(poseLandmarks);
  const showFaceDetailsRef = useRef(showFaceDetails);
  const showPoseDetailsRef = useRef(showPoseDetails);
  const showDiagnosticsRef = useRef(showDiagnostics);

  useEffect(() => {
    onFrameRef.current = onFrame;
  }, [onFrame]);

  useEffect(() => {
    drawLandmarksRef.current = drawLandmarks;
  }, [drawLandmarks]);

  useEffect(() => {
    faceLandmarksRef.current = faceLandmarks;
  }, [faceLandmarks]);

  useEffect(() => {
    poseLandmarksRef.current = poseLandmarks;
  }, [poseLandmarks]);

  useEffect(() => {
    showFaceDetailsRef.current = showFaceDetails;
  }, [showFaceDetails]);

  useEffect(() => {
    showPoseDetailsRef.current = showPoseDetails;
  }, [showPoseDetails]);

  useEffect(() => {
    showDiagnosticsRef.current = showDiagnostics;
  }, [showDiagnostics]);

  useEffect(() => {
    const startRendering = () => {
      const render = () => {
        if (!videoRef.current || !canvasRef.current) return;

        const ctx = canvasRef.current.getContext('2d');
        if (!ctx) return;

        let faceHud: FaceHudInfo | null = null;
        let poseHud: PoseHudInfo | null = null;

        // Draw mirrored video frame + landmarks
        ctx.save();
        ctx.translate(width, 0);
        ctx.scale(-1, 1);
        ctx.drawImage(videoRef.current, 0, 0, width, height);

        // Draw landmarks if provided
        if (drawLandmarksRef.current) {
          if (faceLandmarksRef.current && faceLandmarksRef.current.length > 0) {
            faceHud = drawFaceLandmarks(
              ctx,
              faceLandmarksRef.current,
              width,
              height,
              showFaceDetailsRef.current
            );
          }
          if (poseLandmarksRef.current && poseLandmarksRef.current.length > 0) {
            poseHud = drawPoseLandmarks(
              ctx,
              poseLandmarksRef.current,
              width,
              height,
              showPoseDetailsRef.current
            );
          }
        }
        ctx.restore();

        if (drawLandmarksRef.current) {
          if (faceHud) {
            drawFaceHud(ctx, faceHud);
          }
          if (poseHud) {
            drawPoseHud(ctx, poseHud, width, height);
          }
          if (showDiagnosticsRef.current) {
            drawDiagnostics(ctx, faceLandmarksRef.current, poseLandmarksRef.current, width);
          }
        }

        // Call parent callback with current video/canvas
        onFrameRef.current(videoRef.current, canvasRef.current);

        animationRef.current = requestAnimationFrame(render);
      };

      render();
    };

    if (videoRef.current && stream) {
      videoRef.current.srcObject = stream;
      videoRef.current.onloadedmetadata = () => {
        videoRef.current?.play().catch(err => {
          console.warn('Video play failed:', err);
        });
        if (canvasRef.current) {
          startRendering();
        }
      };
    }

    return () => {
      if (animationRef.current) {
        cancelAnimationFrame(animationRef.current);
        animationRef.current = null;
      }
      if (videoRef.current) videoRef.current.srcObject = null;
    };
  }, [width, height, stream]);

  return (
    <div className="video-canvas-container">
      <video
        ref={videoRef}
        muted
        playsInline
        autoPlay
        style={{ display: 'none' }}
        width={width}
        height={height}
      />
      <canvas
        ref={canvasRef}
        width={width}
        height={height}
        className="video-canvas"
      />
    </div>
  );
};

function drawFaceLandmarks(
  ctx: CanvasRenderingContext2D,
  landmarks: LandmarkPoint[],
  width: number,
  height: number,
  showFaceDetails: boolean
): FaceHudInfo {
  const keyPoints = [1, 33, 133, 263, 362, 61, 291, 199, 13, 14];
  ctx.fillStyle = '#00ff88';

  if (showFaceDetails) {
    for (let i = 0; i < landmarks.length; i += 2) {
      const p = landmarks[i];
      if (!p) continue;
      ctx.beginPath();
      ctx.arc(p.x * width, p.y * height, 1.5, 0, 2 * Math.PI);
      ctx.fill();
    }

    drawClosedPath(ctx, landmarks, FACE_OVAL, width, height, '#00ccff', 1.2);
    drawClosedPath(ctx, landmarks, LEFT_EYE, width, height, '#00ff88', 1.2);
    drawClosedPath(ctx, landmarks, RIGHT_EYE, width, height, '#00ff88', 1.2);
    drawClosedPath(ctx, landmarks, LEFT_IRIS, width, height, '#ffd166', 1.2);
    drawClosedPath(ctx, landmarks, RIGHT_IRIS, width, height, '#ffd166', 1.2);
    drawClosedPath(ctx, landmarks, OUTER_LIPS, width, height, '#ff7b7b', 1.4);
    drawClosedPath(ctx, landmarks, INNER_LIPS, width, height, '#ff4d4d', 1.2);
    drawOpenPath(ctx, landmarks, NOSE_BRIDGE, width, height, '#c9f7f5', 1.2);
    drawOpenPath(ctx, landmarks, LEFT_EYEBROW, width, height, '#00d9ff', 1.2);
    drawOpenPath(ctx, landmarks, RIGHT_EYEBROW, width, height, '#00d9ff', 1.2);
  }

  keyPoints.forEach((idx) => {
    const p = landmarks[idx];
    if (!p) return;
    ctx.beginPath();
    ctx.arc(p.x * width, p.y * height, 2.8, 0, 2 * Math.PI);
    ctx.fillStyle = '#ffffff';
    ctx.fill();
  });

  const mouthState = getMouthState(landmarks);
  const headPose = getHeadPose(landmarks);

  return {
    mouthLabel: mouthState.label,
    mouthRatio: mouthState.ratio,
    yaw: headPose.yaw,
    pitch: headPose.pitch,
    roll: headPose.roll
  };
}

function drawPoseLandmarks(
  ctx: CanvasRenderingContext2D,
  landmarks: LandmarkPoint[],
  width: number,
  height: number,
  showPoseDetails: boolean
): PoseHudInfo {
  const keyPoints = [11, 12, 23, 24, 15, 16, 13, 14, 25, 26];
  const connections = [
    [11, 12], [11, 23], [12, 24], [23, 24],
    [11, 13], [13, 15], [12, 14], [14, 16],
    [23, 25], [24, 26], [25, 27], [26, 28]
  ];

  ctx.strokeStyle = '#ff5a5a';
  ctx.lineWidth = 2.2;
  ctx.beginPath();
  for (const [a, b] of connections) {
    const pa = landmarks[a];
    const pb = landmarks[b];
    if (!isReliable(pa) || !isReliable(pb)) continue;
    ctx.moveTo(pa.x * width, pa.y * height);
    ctx.lineTo(pb.x * width, pb.y * height);
  }
  ctx.stroke();

  if (showPoseDetails) {
    ctx.fillStyle = '#ffd166';
    for (let i = 0; i < landmarks.length; i++) {
      const p = landmarks[i];
      if (!isReliable(p)) continue;
      ctx.beginPath();
      ctx.arc(p.x * width, p.y * height, 2.2, 0, 2 * Math.PI);
      ctx.fill();
    }
  }

  ctx.fillStyle = '#ff5a5a';
  keyPoints.forEach((idx) => {
    const p = landmarks[idx];
    if (!isReliable(p)) return;
    ctx.beginPath();
    ctx.arc(p.x * width, p.y * height, 4, 0, 2 * Math.PI);
    ctx.fill();
  });

  const posture = getPostureDiagnostics(landmarks);
  const needsFraming = drawPoseFramingHint(ctx, landmarks, width, height);

  return {
    postureLabel: posture.postureLabel,
    shoulderTilt: posture.shoulderTilt,
    torsoTilt: posture.torsoTilt,
    needsFraming
  };
}

function drawDiagnostics(
  ctx: CanvasRenderingContext2D,
  faceLandmarks: LandmarkPoint[] | undefined,
  poseLandmarks: LandmarkPoint[] | undefined,
  width: number
) {
  const faceCount = faceLandmarks?.length ?? 0;
  const poseCount = poseLandmarks?.filter(isReliable).length ?? 0;

  ctx.fillStyle = 'rgba(0, 0, 0, 0.7)';
  ctx.fillRect(width - 190, 8, 182, 54);
  ctx.fillStyle = '#ffffff';
  ctx.font = '12px monospace';
  ctx.fillText(`Face points: ${faceCount}`, width - 180, 30);
  ctx.fillText(`Pose points: ${poseCount}`, width - 180, 48);
}

function drawFaceHud(ctx: CanvasRenderingContext2D, info: FaceHudInfo) {
  ctx.fillStyle = 'rgba(0, 0, 0, 0.65)';
  ctx.fillRect(8, 8, 255, 70);
  ctx.fillStyle = '#ffffff';
  ctx.font = '13px monospace';
  ctx.fillText(`Mouth: ${info.mouthLabel}`, 16, 28);
  ctx.fillText(`MAR: ${info.mouthRatio.toFixed(2)}`, 16, 46);
  ctx.fillText(`Head Y/P/R: ${info.yaw.toFixed(0)}/${info.pitch.toFixed(0)}/${info.roll.toFixed(0)}`, 16, 64);
}

function drawPoseHud(ctx: CanvasRenderingContext2D, info: PoseHudInfo, width: number, height: number) {
  ctx.fillStyle = 'rgba(0, 0, 0, 0.65)';
  ctx.fillRect(8, 86, 255, 70);
  ctx.fillStyle = '#ffffff';
  ctx.font = '13px monospace';
  ctx.fillText(`Posture: ${info.postureLabel}`, 16, 106);
  ctx.fillText(`Shoulder tilt: ${info.shoulderTilt.toFixed(1)} deg`, 16, 124);
  ctx.fillText(`Torso tilt: ${info.torsoTilt.toFixed(1)} deg`, 16, 142);

  if (info.needsFraming) {
    ctx.fillStyle = 'rgba(0, 0, 0, 0.7)';
    ctx.fillRect(width * 0.12, height - 46, width * 0.76, 34);
    ctx.fillStyle = '#ffd166';
    ctx.font = '12px sans-serif';
    ctx.fillText('Postur icin burun + omuz kadrajda olmali.', width * 0.14, height - 24);
  }
}

function drawClosedPath(
  ctx: CanvasRenderingContext2D,
  points: LandmarkPoint[],
  indices: number[],
  width: number,
  height: number,
  color: string,
  lineWidth: number
) {
  if (indices.length < 3) return;
  ctx.strokeStyle = color;
  ctx.lineWidth = lineWidth;
  ctx.beginPath();
  const first = points[indices[0]];
  if (!first) return;
  ctx.moveTo(first.x * width, first.y * height);
  for (let i = 1; i < indices.length; i++) {
    const p = points[indices[i]];
    if (!p) continue;
    ctx.lineTo(p.x * width, p.y * height);
  }
  ctx.closePath();
  ctx.stroke();
}

function drawOpenPath(
  ctx: CanvasRenderingContext2D,
  points: LandmarkPoint[],
  indices: number[],
  width: number,
  height: number,
  color: string,
  lineWidth: number
) {
  if (indices.length < 2) return;
  ctx.strokeStyle = color;
  ctx.lineWidth = lineWidth;
  ctx.beginPath();
  let moved = false;
  for (let i = 0; i < indices.length; i++) {
    const p = points[indices[i]];
    if (!p) continue;
    if (!moved) {
      ctx.moveTo(p.x * width, p.y * height);
      moved = true;
    } else {
      ctx.lineTo(p.x * width, p.y * height);
    }
  }
  if (moved) {
    ctx.stroke();
  }
}

function isReliable(p?: LandmarkPoint): p is LandmarkPoint {
  // Webcam/indoor light scenarios often produce low visibility values.
  return !!p && (p.visibility === undefined || p.visibility > 0.12);
}

function getMouthState(landmarks: LandmarkPoint[]): { ratio: number; label: string } {
  const upperLip = landmarks[13];
  const lowerLip = landmarks[14];
  const noseTop = landmarks[10];
  const chin = landmarks[152];

  if (!upperLip || !lowerLip || !noseTop || !chin) {
    return { ratio: 0, label: 'Unknown' };
  }

  const mouthOpen = Math.hypot(lowerLip.x - upperLip.x, lowerLip.y - upperLip.y);
  const faceHeight = Math.max(0.001, Math.hypot(chin.x - noseTop.x, chin.y - noseTop.y));
  const ratio = mouthOpen / faceHeight;

  if (ratio > 0.085) return { ratio, label: 'Open' };
  if (ratio > 0.055) return { ratio, label: 'Semi-open' };
  return { ratio, label: 'Closed' };
}

function getHeadPose(landmarks: LandmarkPoint[]): { yaw: number; pitch: number; roll: number } {
  const nose = landmarks[1];
  const leftEye = landmarks[33];
  const rightEye = landmarks[263];
  const mouthLeft = landmarks[61];
  const mouthRight = landmarks[291];

  if (!nose || !leftEye || !rightEye || !mouthLeft || !mouthRight) {
    return { yaw: 0, pitch: 0, roll: 0 };
  }

  const eyeCenterX = (leftEye.x + rightEye.x) / 2;
  const eyeCenterY = (leftEye.y + rightEye.y) / 2;
  const mouthCenterY = (mouthLeft.y + mouthRight.y) / 2;

  const yaw = (eyeCenterX - 0.5) * 90;
  const pitch = (mouthCenterY - eyeCenterY) * 180;
  const roll = Math.atan2(rightEye.y - leftEye.y, rightEye.x - leftEye.x) * 57.2958;

  return { yaw, pitch, roll };
}

function getPostureDiagnostics(landmarks: LandmarkPoint[]): {
  shoulderTilt: number;
  torsoTilt: number;
  postureLabel: string;
} {
  const leftShoulder = landmarks[11];
  const rightShoulder = landmarks[12];
  const nose = landmarks[0];
  const hasShoulders = isReliable(leftShoulder) && isReliable(rightShoulder);
  const hasNose = isReliable(nose);

  if (!hasShoulders || !hasNose) {
    return { shoulderTilt: 0, torsoTilt: 0, postureLabel: 'Need upper body in frame' };
  }

  const shoulderTilt = Math.abs(
    Math.atan2(rightShoulder.y - leftShoulder.y, rightShoulder.x - leftShoulder.x) * 57.2958
  );

  const shoulderCenter = {
    x: (leftShoulder.x + rightShoulder.x) / 2,
    y: (leftShoulder.y + rightShoulder.y) / 2
  };
  // Estimate upper-body lean only from nose offset to shoulder center.
  const torsoTilt = Math.abs((nose.x - shoulderCenter.x) * 120);

  const postureLabel =
    shoulderTilt < 7 && torsoTilt < 12
      ? 'Upright'
      : shoulderTilt < 12 && torsoTilt < 18
        ? 'Needs correction'
        : 'Slouch/lean';

  return { shoulderTilt, torsoTilt, postureLabel };
}

function drawPoseFramingHint(
  ctx: CanvasRenderingContext2D,
  landmarks: LandmarkPoint[],
  width: number,
  height: number
): boolean {
  const leftShoulder = landmarks[11];
  const rightShoulder = landmarks[12];
  const nose = landmarks[0];
  const hasShoulders = isReliable(leftShoulder) && isReliable(rightShoulder);
  const hasNose = isReliable(nose);

  if (hasShoulders && hasNose) return false;

  ctx.strokeStyle = 'rgba(255, 193, 7, 0.9)';
  ctx.lineWidth = 2;
  ctx.setLineDash([8, 6]);
  ctx.strokeRect(width * 0.2, height * 0.08, width * 0.6, height * 0.84);
  ctx.setLineDash([]);
  return true;
}

const FACE_OVAL = [
  10, 338, 297, 332, 284, 251, 389, 356, 454, 323, 361, 288, 397,
  365, 379, 378, 400, 377, 152, 148, 176, 149, 150, 136, 172, 58,
  132, 93, 234, 127, 162, 21, 54, 103, 67, 109
];
const LEFT_EYE = [33, 7, 163, 144, 145, 153, 154, 155, 133, 173, 157, 158, 159, 160, 161, 246];
const RIGHT_EYE = [362, 382, 381, 380, 374, 373, 390, 249, 263, 466, 388, 387, 386, 385, 384, 398];
const LEFT_IRIS = [468, 469, 470, 471, 472];
const RIGHT_IRIS = [473, 474, 475, 476, 477];
const LEFT_EYEBROW = [70, 63, 105, 66, 107, 55, 65, 52, 53, 46];
const RIGHT_EYEBROW = [336, 296, 334, 293, 300, 285, 295, 282, 283, 276];
const OUTER_LIPS = [61, 146, 91, 181, 84, 17, 314, 405, 321, 375, 291, 308, 324, 318, 402, 317, 14, 87, 178, 88, 95, 78];
const INNER_LIPS = [78, 191, 80, 81, 82, 13, 312, 311, 310, 415, 308, 324, 318, 402, 317, 14, 87, 178];
const NOSE_BRIDGE = [6, 197, 195, 5, 4, 1, 19, 94, 2];
