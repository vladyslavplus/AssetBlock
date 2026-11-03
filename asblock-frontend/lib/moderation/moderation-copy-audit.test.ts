import { readFile, readdir } from 'node:fs/promises'
import path from 'node:path'
import { describe, expect, it } from 'vitest'
import { ModerationApiError, moderationErrorMessage } from './moderation-query'

async function sourceFiles(directory: string): Promise<string[]> {
  const entries = await readdir(directory, { withFileTypes: true })
  const nested = await Promise.all(
    entries.map(async (entry) => {
      const entryPath = path.join(directory, entry.name)
      if (entry.isDirectory()) return sourceFiles(entryPath)
      if (!/\.(?:ts|tsx)$/.test(entry.name) || /\.(?:test|spec)\.(?:ts|tsx)$/.test(entry.name)) {
        return []
      }
      return [entryPath]
    }),
  )
  return nested.flat()
}

const reviewLoopCode =
  /\b(?:theft|stolen|steal(?:ing)?|plagiarism|plagiarist|plagiarized|plagiarize|illegal|infringing|infringement)\b|\blegal\s+(?:verdict|determination|finding)\b|\b\d+(?:\.\d+)?\s*(?:%|percent)\s*(?:similarity|similar|match|overlap|confidence|copied|copying|duplicate|identical|proves?|confirms?|shows?|indicates?|verdict)\b|\b(?:similarity|similar|match|overlap|confidence)(?:\s+score)?\s*(?:is\s*)?\d+(?:\.\d+)?\s*%/i

describe('moderation copy audit', () => {
  it('checks nonempty code-free output against backend review-loop error constants', async () => {
    const backendContract = await readFile(
      path.join(process.cwd(), '../asblock-backend/AssetBlock.Domain/Core/Constants/ErrorCodes.cs'),
      'utf8',
    )
    const codes = [...new Set(backendContract.match(/\bERR_[A-Z0-9_]+\b/g) ?? [])].filter((code) =>
      /^ERR_(?:MODERATION|REVIEW|COVERAGE|FINDING|SELLER_EVIDENCE|VERSION_FILE_CHECKS|DECLARATION_INCOMPLETE|ANALYSIS_NOT_AVAILABLE|ANALYSIS_REPORT_NOT_TRUSTED)/.test(
        code,
      ),
    )

    expect(codes.length).toBeGreaterThan(0)
    for (const code of codes) {
      const message = moderationErrorMessage(
        new ModerationApiError(422, 'Review action failed (422).', code),
      )
      expect(message, code).toBeTruthy()
      expect(message).not.toMatch(/ERR_[A-Z0-9_]+/)
    }
  })

  it('uses a generic fallback for an unknown review error without forcing service-unavailable status', () => {
    expect(
      moderationErrorMessage(
        new ModerationApiError(422, 'Review action failed (422).', 'ERR_FUTURE_REVIEW_CODE'),
      ),
    ).toBe('Review action failed (422).')
  })

  it('detects prohibited verdict language and percentage-as-verdict phrases', () => {
    const prohibited = [
      'The model says this file is stolen.',
      'This submission is illegal.',
      'The asset is infringing.',
      'The model found infringement.',
      'This is plagiarism.',
      'This is a legal verdict.',
      '92% similarity proves copying.',
      'The model reports a 92 percent match.',
      'The match score is 92%.',
      'A 92% match confirms infringement.',
      'The confidence is 92%.',
    ]
    for (const copy of prohibited) expect(copy).toMatch(reviewLoopCode)
  })

  it('allows moderator-only raw scores and descriptive rights notices', () => {
    const allowed = [
      'Moderator evidence: semantic score 0.92; token score 0.73.',
      'Contact the rights holder to report a concern.',
    ]
    for (const copy of allowed) expect(copy).not.toMatch(reviewLoopCode)
  })

  it('keeps verdict language out of all app, component, and library production copy', async () => {
    const roots = ['app', 'components', 'lib'].map((relative) => path.join(process.cwd(), relative))
    const files = (await Promise.all(roots.map(sourceFiles))).flat()
    const violations: string[] = []

    for (const file of files) {
      const source = await readFile(file, 'utf8')
      if (reviewLoopCode.test(source)) violations.push(path.relative(process.cwd(), file))
    }

    expect(violations).toEqual([])
  })
})
