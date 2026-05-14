import { StitchToolClient } from '@google/stitch-sdk'

const apiKey = import.meta.env.VITE_STITCH_API_KEY as string | undefined

let _client: StitchToolClient | null = null

function getClient(): StitchToolClient {
  if (!_client) {
    if (!apiKey) {
      throw new Error('VITE_STITCH_API_KEY is not set. Add it to your .env file.')
    }
    _client = new StitchToolClient({ apiKey })
  }
  return _client
}

export const StitchService = {
  isConfigured(): boolean {
    return !!apiKey
  },

  getClient
}

export default StitchService
