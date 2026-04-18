import { Calibration, FaceLandmarks, HeadPose, PoseLandmarks } from './types'

function dist2d(a?: { x: number; y: number }, b?: { x: number; y: number }): number {
  if (!a || !b) return 0
  return Math.hypot(a.x - b.x, a.y - b.y)
}

function mean(values: number[]): number {
  if (values.length === 0) return 0
  return values.reduce((sum, value) => sum + value, 0) / values.length
}

function stddev(values: number[]): number {
  if (values.length <= 1) return 0
  const m = mean(values)
  const variance = values.reduce((sum, value) => sum + (value - m) ** 2, 0) / values.length
  return Math.sqrt(variance)
}

function clamp01(value: number): number {
  return Math.max(0, Math.min(1, value))
}

export function smooth(prev: number, next: number, alpha: number): number {
  const safeAlpha = clamp01(alpha)
  return prev + (next - prev) * safeAlpha
}

export function normalizeClamp(x: number, min: number, max: number): number {
  if (max <= min) return 0
  return clamp01((x - min) / (max - min))
}

export function computeEyeOpenness(faceLandmarks: FaceLandmarks): number {
  if (!faceLandmarks || faceLandmarks.length < 387) return 0.5

  const leftVertical = dist2d(faceLandmarks[159], faceLandmarks[145])
  const leftHorizontal = dist2d(faceLandmarks[33], faceLandmarks[133])
  const rightVertical = dist2d(faceLandmarks[386], faceLandmarks[374])
  const rightHorizontal = dist2d(faceLandmarks[362], faceLandmarks[263])

  if (leftHorizontal <= 0 || rightHorizontal <= 0) return 0.5

  const leftRatio = leftVertical / leftHorizontal
  const rightRatio = rightVertical / rightHorizontal
  const ratio = (leftRatio + rightRatio) / 2

  return normalizeClamp(ratio, 0.12, 0.30)
}

export function computeHeadPose(faceLandmarks: FaceLandmarks): HeadPose {
  if (!faceLandmarks || faceLandmarks.length < 264) return { yaw: 0, pitch: 0 }

  const leftEye = faceLandmarks[33]
  const rightEye = faceLandmarks[263]
  const nose = faceLandmarks[1]
  const mouthTop = faceLandmarks[13]
  const mouthBottom = faceLandmarks[14]

  if (!leftEye || !rightEye || !nose || !mouthTop || !mouthBottom) {
    return { yaw: 0, pitch: 0 }
  }

  const eyeCenterX = (leftEye.x + rightEye.x) / 2
  const eyeCenterY = (leftEye.y + rightEye.y) / 2
  const mouthCenterY = (mouthTop.y + mouthBottom.y) / 2
  const eyeDistance = Math.max(0.001, dist2d(leftEye, rightEye))

  const yaw = (nose.x - eyeCenterX) / eyeDistance
  const pitch = (mouthCenterY - eyeCenterY) / eyeDistance - 0.35

  return { yaw, pitch }
}

export function computeHeadJitter(headPoseHistory: HeadPose[]): number {
  if (!headPoseHistory || headPoseHistory.length < 3) return 0

  const yawSeries = headPoseHistory.map((pose) => pose.yaw)
  const pitchSeries = headPoseHistory.map((pose) => pose.pitch)
  const combinedStd = stddev(yawSeries) + stddev(pitchSeries)

  return normalizeClamp(combinedStd, 0.01, 0.14)
}

export function computePostureScore(poseLandmarks: PoseLandmarks): number {
  if (!poseLandmarks || poseLandmarks.length < 25) return 0.5

  const leftShoulder = poseLandmarks[11]
  const rightShoulder = poseLandmarks[12]
  const leftHip = poseLandmarks[23]
  const rightHip = poseLandmarks[24]

  if (!leftShoulder || !rightShoulder || !leftHip || !rightHip) return 0.5

  const shoulderWidth = Math.max(0.001, dist2d(leftShoulder, rightShoulder))
  const shoulderTilt = Math.abs(leftShoulder.y - rightShoulder.y) / shoulderWidth

  const shoulderCenterX = (leftShoulder.x + rightShoulder.x) / 2
  const shoulderCenterY = (leftShoulder.y + rightShoulder.y) / 2
  const hipCenterX = (leftHip.x + rightHip.x) / 2
  const hipCenterY = (leftHip.y + rightHip.y) / 2
  const torsoLength = Math.max(0.001, Math.hypot(shoulderCenterX - hipCenterX, shoulderCenterY - hipCenterY))
  const torsoLean = Math.abs(shoulderCenterX - hipCenterX) / torsoLength

  const levelScore = 1 - normalizeClamp(shoulderTilt, 0.02, 0.25)
  const leanScore = 1 - normalizeClamp(torsoLean, 0.02, 0.30)

  return clamp01(levelScore * 0.5 + leanScore * 0.5)
}

export function computeFidgetScore(poseLandmarksHistory: PoseLandmarks[]): number {
  if (!poseLandmarksHistory || poseLandmarksHistory.length < 3) return 0

  let totalEnergy = 0
  let steps = 0

  for (let i = 1; i < poseLandmarksHistory.length; i++) {
    const prev = poseLandmarksHistory[i - 1]
    const curr = poseLandmarksHistory[i]

    const prevLs = prev[11]
    const prevRs = prev[12]
    const currLs = curr[11]
    const currRs = curr[12]
    const shoulderWidth = Math.max(0.001, dist2d(prevLs, prevRs) || dist2d(currLs, currRs))
    if (!Number.isFinite(shoulderWidth) || shoulderWidth <= 0) continue

    const trackedJoints: Array<[number, number]> = [
      [15, 15],
      [16, 16],
      [13, 13],
      [14, 14]
    ]

    let frameEnergy = 0
    let joints = 0
    for (const [prevIdx, currIdx] of trackedJoints) {
      const p = prev[prevIdx]
      const c = curr[currIdx]
      if (!p || !c) continue
      frameEnergy += dist2d(p, c) / shoulderWidth
      joints += 1
    }

    if (joints > 0) {
      totalEnergy += frameEnergy / joints
      steps += 1
    }
  }

  if (steps === 0) return 0
  const avgEnergy = totalEnergy / steps
  return normalizeClamp(avgEnergy, 0.01, 0.20)
}

export function computeEyeContactScore(headPose: HeadPose, calibration: Calibration | null): number {
  const baselineYaw = calibration?.baselineYaw ?? 0
  const baselinePitch = calibration?.baselinePitch ?? 0

  const yawDelta = Math.abs(headPose.yaw - baselineYaw)
  const pitchDelta = Math.abs(headPose.pitch - baselinePitch)

  const yawPenalty = normalizeClamp(yawDelta, 0.04, 0.30)
  const pitchPenalty = normalizeClamp(pitchDelta, 0.04, 0.35)
  const combinedPenalty = yawPenalty * 0.65 + pitchPenalty * 0.35

  return clamp01(1 - combinedPenalty)
}
