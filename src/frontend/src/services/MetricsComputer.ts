/**
 * Real-time metric computation from MediaPipe landmarks
 */

export type EmotionLabel =
  | 'Neutral'
  | 'Happy'
  | 'Sad'
  | 'Angry'
  | 'Surprised'
  | 'Tense'
  | 'LowEnergy';

export interface Metrics {
  eyeContact: number; // 0-100
  headStability: number; // 0-100
  posture: number; // 0-100
  fidget: number; // 0-100
  eyeOpenness: number; // 0-100
  emotion: EmotionLabel;
  timestamp: number;
}

export interface BehaviorStats {
  dominantEmotion: EmotionLabel;
  currentEmotion: EmotionLabel;
  emotionPercentages: Record<EmotionLabel, number>;
  eyeOpenPercent: number;
  eyeClosedPercent: number;
  blinkCount: number;
  currentEyesOpen: boolean;
}

const EMOTIONS: EmotionLabel[] = ['Neutral', 'Happy', 'Sad', 'Angry', 'Surprised', 'Tense', 'LowEnergy'];

function computeHeadPose(faceLandmarks: any[]): { yaw: number; pitch: number; roll: number } {
  if (!faceLandmarks || faceLandmarks.length < 468) {
    return { yaw: 0, pitch: 0, roll: 0 };
  }

  const leftEye = faceLandmarks[33];
  const rightEye = faceLandmarks[263];
  const leftMouth = faceLandmarks[61];
  const rightMouth = faceLandmarks[291];
  if (!leftEye || !rightEye || !leftMouth || !rightMouth) {
    return { yaw: 0, pitch: 0, roll: 0 };
  }

  const eyeCenterX = (leftEye.x + rightEye.x) / 2;
  const yaw = (eyeCenterX - 0.5) * 90;
  const eyeCenterY = (leftEye.y + rightEye.y) / 2;
  const mouthCenterY = (leftMouth.y + rightMouth.y) / 2;
  const pitch = (mouthCenterY - eyeCenterY) * 180;
  const roll = Math.atan2(rightEye.y - leftEye.y, rightEye.x - leftEye.x) * 57.3;
  return { yaw, pitch, roll };
}

function dist(a: any, b: any): number {
  if (!a || !b) return 0;
  return Math.hypot(a.x - b.x, a.y - b.y);
}

function clamp01(v: number): number {
  return Math.max(0, Math.min(1, v));
}

export class MetricsComputer {
  private headPoseHistory: Array<{ yaw: number; pitch: number; roll: number }> = [];
  private handMovementHistory: number[] = [];
  private emotionHistory: EmotionLabel[] = [];
  private eyeContactFrames = 0;
  private totalFrames = 0;

  private eyeOpenFrames = 0;
  private eyeClosedFrames = 0;
  private blinkCount = 0;
  private wasEyesOpen = true;
  private closedRunFrames = 0;

  private emotionCounts: Record<EmotionLabel, number> = {
    Neutral: 0,
    Happy: 0,
    Sad: 0,
    Angry: 0,
    Surprised: 0,
    Tense: 0,
    LowEnergy: 0
  };
  private lastEmotion: EmotionLabel = 'Neutral';
  private lastEyesOpen = true;

  computeFrame(faceLandmarks: any[], poseLandmarks: any[]): Metrics {
    this.totalFrames++;

    const eyeContact = this.computeEyeContactScore(faceLandmarks);
    const headStability = this.computeHeadStabilityScore(faceLandmarks);
    const posture = this.computePostureScore(poseLandmarks);
    const fidget = this.computeFidgetScore(poseLandmarks);
    const eyeOpenness = this.computeEyeOpennessScore(faceLandmarks);

    const eyesOpen = eyeOpenness >= 45;
    this.updateEyeState(eyesOpen);

    const rawEmotion = this.computeEmotion(faceLandmarks, eyeOpenness);
    const emotion = this.smoothEmotion(rawEmotion);
    this.emotionCounts[emotion] += 1;
    this.lastEmotion = emotion;

    return {
      eyeContact,
      headStability,
      posture,
      fidget,
      eyeOpenness,
      emotion,
      timestamp: Date.now()
    };
  }

  private computeEyeContactScore(faceLandmarks: any[]): number {
    if (!faceLandmarks || faceLandmarks.length === 0) return 50;
    const { yaw, pitch } = computeHeadPose(faceLandmarks);
    const yawPenalty = Math.max(0, Math.abs(yaw) - 20) / 45;
    const pitchPenalty = Math.max(0, Math.abs(pitch) - 15) / 45;
    const score = 100 - (yawPenalty + pitchPenalty) * 50;
    if (score > 80) this.eyeContactFrames++;
    return Math.max(0, Math.min(100, score));
  }

  private computeHeadStabilityScore(faceLandmarks: any[]): number {
    if (!faceLandmarks || faceLandmarks.length === 0) return 50;
    const pose = computeHeadPose(faceLandmarks);
    this.headPoseHistory.push(pose);
    if (this.headPoseHistory.length > 30) this.headPoseHistory.shift();
    if (this.headPoseHistory.length < 2) return 50;

    const yawValues = this.headPoseHistory.map((p) => p.yaw);
    const pitchValues = this.headPoseHistory.map((p) => p.pitch);
    const yawMean = yawValues.reduce((a, b) => a + b, 0) / yawValues.length;
    const pitchMean = pitchValues.reduce((a, b) => a + b, 0) / pitchValues.length;
    const yawVariance = yawValues.reduce((sum, y) => sum + (y - yawMean) ** 2, 0) / yawValues.length;
    const pitchVariance = pitchValues.reduce((sum, p) => sum + (p - pitchMean) ** 2, 0) / pitchValues.length;
    const totalVariance = Math.sqrt(yawVariance + pitchVariance);
    return Math.min(100, Math.max(0, 100 - (totalVariance / 15) * 100));
  }

  private computePostureScore(poseLandmarks: any[]): number {
    if (!poseLandmarks || poseLandmarks.length === 0) return 50;
    const leftShoulder = poseLandmarks[11];
    const rightShoulder = poseLandmarks[12];
    const nose = poseLandmarks[0];
    if (!leftShoulder || !rightShoulder || !nose) return 50;

    const shoulderHeightDiff = Math.abs(leftShoulder.y - rightShoulder.y);
    const shoulderAlignmentScore = Math.max(0, 100 - shoulderHeightDiff * 500);
    const shoulderCenterX = (leftShoulder.x + rightShoulder.x) / 2;
    const lean = Math.abs(nose.x - shoulderCenterX);
    const leanScore = Math.max(0, 100 - lean * 350);
    return Math.min(100, (shoulderAlignmentScore + leanScore) / 2);
  }

  private computeFidgetScore(poseLandmarks: any[]): number {
    if (!poseLandmarks || poseLandmarks.length === 0) return 50;
    const leftWrist = poseLandmarks[15];
    const rightWrist = poseLandmarks[16];
    if (!leftWrist || !rightWrist) return 50;

    const movement = Math.hypot(rightWrist.x - leftWrist.x, rightWrist.y - leftWrist.y);
    this.handMovementHistory.push(movement);
    if (this.handMovementHistory.length > 30) this.handMovementHistory.shift();
    if (this.handMovementHistory.length < 2) return 50;

    const avgMovement = this.handMovementHistory.reduce((a, b) => a + b, 0) / this.handMovementHistory.length;
    return Math.min(100, Math.max(0, 100 - (avgMovement / 0.3) * 100));
  }

  private computeEyeOpennessScore(faceLandmarks: any[]): number {
    if (!faceLandmarks || faceLandmarks.length < 387) return 50;
    const leftVertical = dist(faceLandmarks[159], faceLandmarks[145]);
    const leftHorizontal = dist(faceLandmarks[33], faceLandmarks[133]);
    const rightVertical = dist(faceLandmarks[386], faceLandmarks[374]);
    const rightHorizontal = dist(faceLandmarks[362], faceLandmarks[263]);
    if (leftHorizontal <= 0 || rightHorizontal <= 0) return 50;

    const avgEar = (leftVertical / leftHorizontal + rightVertical / rightHorizontal) / 2;
    return clamp01((avgEar - 0.13) / (0.30 - 0.13)) * 100;
  }

  private computeEmotion(faceLandmarks: any[], eyeOpenness: number): EmotionLabel {
    if (!faceLandmarks || faceLandmarks.length < 388) return 'Neutral';

    const mouthLeft = faceLandmarks[61];
    const mouthRight = faceLandmarks[291];
    const upperLip = faceLandmarks[13];
    const lowerLip = faceLandmarks[14];
    const leftEyeOuter = faceLandmarks[33];
    const rightEyeOuter = faceLandmarks[263];
    const leftBrowInner = faceLandmarks[105];
    const rightBrowInner = faceLandmarks[334];
    const leftEyeTop = faceLandmarks[159];
    const rightEyeTop = faceLandmarks[386];

    const faceWidth = Math.max(dist(leftEyeOuter, rightEyeOuter), 0.0001);
    const mouthWidth = dist(mouthLeft, mouthRight);
    const mouthOpen = dist(upperLip, lowerLip);
    const smileRatio = mouthWidth / faceWidth;
    const mouthOpenRatio = mouthOpen / faceWidth;

    const browInnerDistance = dist(leftBrowInner, rightBrowInner) / faceWidth;
    const leftBrowEyeDist = dist(leftBrowInner, leftEyeTop) / faceWidth;
    const rightBrowEyeDist = dist(rightBrowInner, rightEyeTop) / faceWidth;
    const avgBrowEyeDist = (leftBrowEyeDist + rightBrowEyeDist) / 2;
    const lipPressed = mouthOpenRatio < 0.035;
    const veryLowEnergyEyes = eyeOpenness < 22;

    if (mouthOpenRatio > 0.18 && eyeOpenness > 68 && avgBrowEyeDist > 0.12) return 'Surprised';
    if (smileRatio > 0.50 && mouthOpenRatio > 0.02 && mouthOpenRatio < 0.16 && eyeOpenness > 35) return 'Happy';
    if (veryLowEnergyEyes && mouthOpenRatio < 0.07) return 'LowEnergy';
    if (browInnerDistance < 0.70 && lipPressed && eyeOpenness < 62) return 'Angry';
    if (smileRatio < 0.44 && mouthOpenRatio < 0.08 && eyeOpenness < 45 && avgBrowEyeDist > 0.10) return 'Sad';
    if (lipPressed && eyeOpenness >= 35 && eyeOpenness < 65 && smileRatio < 0.48) return 'Tense';
    return 'Neutral';
  }

  private smoothEmotion(rawEmotion: EmotionLabel): EmotionLabel {
    this.emotionHistory.push(rawEmotion);
    if (this.emotionHistory.length > 12) this.emotionHistory.shift();
    const counts = this.emotionHistory.reduce((acc, e) => {
      acc[e] = (acc[e] ?? 0) + 1;
      return acc;
    }, {} as Record<EmotionLabel, number>);
    return EMOTIONS.reduce((best, e) => (counts[e] ?? 0) > (counts[best] ?? 0) ? e : best, rawEmotion);
  }

  private updateEyeState(eyesOpen: boolean): void {
    this.lastEyesOpen = eyesOpen;
    if (eyesOpen) {
      this.eyeOpenFrames++;
      if (!this.wasEyesOpen && this.closedRunFrames >= 1 && this.closedRunFrames <= 6) this.blinkCount++;
      this.closedRunFrames = 0;
    } else {
      this.eyeClosedFrames++;
      this.closedRunFrames++;
    }
    this.wasEyesOpen = eyesOpen;
  }

  getEyeContactPercentage(): number {
    if (this.totalFrames === 0) return 0;
    return (this.eyeContactFrames / this.totalFrames) * 100;
  }

  getBehaviorStats(): BehaviorStats {
    const emotionTotal = EMOTIONS.reduce((sum, key) => sum + this.emotionCounts[key], 0);
    const eyeTotal = this.eyeOpenFrames + this.eyeClosedFrames;

    const emotionPercentages = EMOTIONS.reduce((acc, key) => {
      acc[key] = emotionTotal > 0 ? (this.emotionCounts[key] / emotionTotal) * 100 : 0;
      return acc;
    }, {} as Record<EmotionLabel, number>);

    const dominantEmotion = EMOTIONS.reduce((best, key) =>
      this.emotionCounts[key] > this.emotionCounts[best] ? key : best
    , 'Neutral' as EmotionLabel);

    return {
      dominantEmotion,
      currentEmotion: this.lastEmotion,
      emotionPercentages,
      eyeOpenPercent: eyeTotal > 0 ? (this.eyeOpenFrames / eyeTotal) * 100 : 0,
      eyeClosedPercent: eyeTotal > 0 ? (this.eyeClosedFrames / eyeTotal) * 100 : 0,
      blinkCount: this.blinkCount,
      currentEyesOpen: this.lastEyesOpen
    };
  }

  reset(): void {
    this.headPoseHistory = [];
    this.handMovementHistory = [];
    this.emotionHistory = [];
    this.eyeContactFrames = 0;
    this.totalFrames = 0;
    this.eyeOpenFrames = 0;
    this.eyeClosedFrames = 0;
    this.blinkCount = 0;
    this.wasEyesOpen = true;
    this.closedRunFrames = 0;
    this.emotionCounts = {
      Neutral: 0,
      Happy: 0,
      Sad: 0,
      Angry: 0,
      Surprised: 0,
      Tense: 0,
      LowEnergy: 0
    };
    this.lastEmotion = 'Neutral';
    this.lastEyesOpen = true;
  }
}
