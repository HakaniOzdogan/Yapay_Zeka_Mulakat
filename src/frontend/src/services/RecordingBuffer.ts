const DB_NAME = 'interview-recordings'
const STORE_NAME = 'pending'
const DB_VERSION = 1

export interface RecordingEntry {
  key: string
  sessionId: string
  questionOrder: number
  blob: Blob
  mimeType: string
  startMs?: number
  endMs?: number
  savedAt: number
}

function openDb(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB_NAME, DB_VERSION)
    req.onupgradeneeded = () => {
      req.result.createObjectStore(STORE_NAME, { keyPath: 'key' })
    }
    req.onsuccess = () => resolve(req.result)
    req.onerror = () => reject(req.error)
  })
}

export async function saveRecording(entry: Omit<RecordingEntry, 'savedAt'>): Promise<void> {
  try {
    const db = await openDb()
    await new Promise<void>((resolve, reject) => {
      const tx = db.transaction(STORE_NAME, 'readwrite')
      tx.objectStore(STORE_NAME).put({ ...entry, savedAt: Date.now() })
      tx.oncomplete = () => resolve()
      tx.onerror = () => reject(tx.error)
      tx.onabort = () => reject(tx.error)
    })
    db.close()
  } catch {
    // IndexedDB unavailable (private browsing, quota exceeded, etc.)
  }
}

export async function removeRecording(key: string): Promise<void> {
  try {
    const db = await openDb()
    await new Promise<void>((resolve, reject) => {
      const tx = db.transaction(STORE_NAME, 'readwrite')
      tx.objectStore(STORE_NAME).delete(key)
      tx.oncomplete = () => resolve()
      tx.onerror = () => reject(tx.error)
    })
    db.close()
  } catch {
    // no-op
  }
}

export async function getPendingRecordings(): Promise<RecordingEntry[]> {
  try {
    const db = await openDb()
    const result = await new Promise<RecordingEntry[]>((resolve, reject) => {
      const tx = db.transaction(STORE_NAME, 'readonly')
      const req = tx.objectStore(STORE_NAME).getAll()
      req.onsuccess = () => resolve(req.result as RecordingEntry[])
      req.onerror = () => reject(req.error)
    })
    db.close()
    return result
  } catch {
    return []
  }
}
