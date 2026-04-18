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

export interface RollingBuffer<T> {
  push(value: T): void
  values(): T[]
  clear(): void
  size(): number
}
