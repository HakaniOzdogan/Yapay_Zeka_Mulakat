import { RollingBuffer } from './types'

export class FixedRollingBuffer<T> implements RollingBuffer<T> {
  private readonly _maxLength: number
  private readonly _items: T[] = []

  constructor(maxLength: number) {
    this._maxLength = Math.max(1, Math.floor(maxLength))
  }

  push(value: T): void {
    this._items.push(value)
    if (this._items.length > this._maxLength) {
      this._items.shift()
    }
  }

  values(): T[] {
    return this._items
  }

  clear(): void {
    this._items.length = 0
  }

  size(): number {
    return this._items.length
  }
}
