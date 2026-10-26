import { describe, expect, it } from 'vitest'
import { formatSessionDuration } from './sessionDuration'

describe('formatSessionDuration', () => {
  it('formats hours and minutes', () => {
    expect(formatSessionDuration(3660)).toBe('1h 1m')
    expect(formatSessionDuration(7200)).toBe('2h 0m')
  })

  it('formats minutes and seconds below one hour', () => {
    expect(formatSessionDuration(90)).toBe('1m 30s')
    expect(formatSessionDuration(59)).toBe('0m 59s')
    expect(formatSessionDuration(0)).toBe('0m 0s')
  })

  it('renders unknown or negative as dash', () => {
    expect(formatSessionDuration(null)).toBe('—')
    expect(formatSessionDuration(undefined)).toBe('—')
    expect(formatSessionDuration(-1)).toBe('—')
  })
})
