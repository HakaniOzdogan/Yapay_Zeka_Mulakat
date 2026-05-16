export interface FaceLandmark {
  x: number
  y: number
  z?: number
}

export interface PoseLandmark {
  x: number
  y: number
  z?: number
  visibility?: number
}

export type FaceLandmarks = FaceLandmark[]
export type PoseLandmarks = PoseLandmark[]

export interface HeadPose {
  yaw: number
  pitch: number
}

export interface Calibration {
  baselineYaw: number
  baselinePitch: number
  baselineEyeOpenness: number
  startedAtMs: number
  completedAtMs: number
  sampleCount: number
}

export interface VisionMetrics {
  eyeContact: number
  posture: number
  fidget: number
  headJitter: number
  eyeOpenness: number
  calibrated: boolean
}

// Blendshape names from ARKit (52 total)
export const BLENDSHAPE_NAMES = [
  'browDownLeft','browDownRight','browInnerUp','browOuterUpLeft','browOuterUpRight',
  'cheekPuff','cheekSquintLeft','cheekSquintRight',
  'eyeBlinkLeft','eyeBlinkRight','eyeLookDownLeft','eyeLookDownRight',
  'eyeLookInLeft','eyeLookInRight','eyeLookOutLeft','eyeLookOutRight',
  'eyeLookUpLeft','eyeLookUpRight','eyeSquintLeft','eyeSquintRight',
  'eyeWideLeft','eyeWideRight','jawForward','jawLeft','jawOpen','jawRight',
  'mouthClose','mouthDimpleLeft','mouthDimpleRight','mouthFrownLeft','mouthFrownRight',
  'mouthFunnel','mouthLeft','mouthLowerDownLeft','mouthLowerDownRight',
  'mouthPressLeft','mouthPressRight','mouthPucker','mouthRight',
  'mouthRollLower','mouthRollUpper','mouthShrugLower','mouthShrugUpper',
  'mouthSmileLeft','mouthSmileRight','mouthStretchLeft','mouthStretchRight',
  'mouthUpperUpLeft','mouthUpperUpRight','noseSneerLeft','noseSneerRight','tongueOut'
] as const

export type BlendshapeName = typeof BLENDSHAPE_NAMES[number]
export type BlendshapeVector = Partial<Record<BlendshapeName, number>>

export interface DerivedSignals {
  // Smile
  smileIntensity: number      // 0–1
  isDuchenne: boolean         // true = genuine (cheek muscles involved)
  // Stress indicators
  lipPress: number            // 0–1 mouth pressure
  browFurrow: number          // 0–1 brow concern/focus
  jawTension: number          // 0–1 jaw openness signal
  // Composite indices (0–1)
  arousalIndex: number        // general facial activation level
  stressSignal: number        // negative high-arousal composite
  discomfortSignal: number    // discomfort composite
}

export interface RollingBuffer<T> {
  push(value: T): void
  values(): T[]
  clear(): void
  size(): number
}
