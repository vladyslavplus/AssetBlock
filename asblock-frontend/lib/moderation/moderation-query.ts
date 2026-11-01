'use client'

import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { getApiErrorMessage, parseApiErrorBody, readApiResponseBody } from '@/lib/http/api-errors'
import { moderationBackendPath } from './moderation-routes'
import {
  claimChangeResponseSchema,
  coverageAssessmentResponseSchema,
  coverageQueuePageSchema,
  moderationCaseSummarySchema,
  moderationDecisionResponseSchema,
  moderatorQueuePageSchema,
  restrictedEvidenceResponseSchema,
  type ClaimChangeRequest,
  type CoverageAssessmentRequest,
  type ModerationDecisionRequest,
  type RestrictedEvidenceKind,
} from './moderation-schemas'

export interface ModerationQueueFilters {
  state?: 'SUBMITTED' | 'IN_REVIEW'
  claimFilter?: 'ALL' | 'MINE' | 'UNCLAIMED'
  pageSize?: number
}

export const moderationKeys = {
  all: ['moderation'] as const,
  coverageRoot: ['moderation', 'coverage'] as const,
  coverage: (page = 1) => [...moderationKeys.coverageRoot, page] as const,
  queue: (filters: ModerationQueueFilters = {}) =>
    [...moderationKeys.all, 'queue', filters] as const,
  case: (submissionId: string) => [...moderationKeys.all, 'case', submissionId] as const,
}

export class ModerationApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
    readonly code?: string,
    readonly blockerCodes: string[] = [],
  ) {
    super(message)
    this.name = 'ModerationApiError'
  }
}

async function readSuccess<T>(
  response: Response,
  schema: { safeParse: (value: unknown) => { success: boolean; data?: T } },
  label: string,
): Promise<T> {
  const body = await readApiResponseBody(response)
  if (!response.ok) {
    const apiError = parseApiErrorBody(body)
    throw new ModerationApiError(
      response.status,
      getApiErrorMessage(body, `${label} failed (${response.status}).`),
      apiError?.code,
      apiError?.blockerCodes,
    )
  }
  const parsed = schema.safeParse(body)
  if (!parsed.success || parsed.data === undefined) {
    throw new ModerationApiError(502, `Invalid ${label.toLowerCase()} response from server.`)
  }
  return parsed.data
}

export async function fetchModerationQueue(
  filters: ModerationQueueFilters = {},
  cursor?: string,
  signal?: AbortSignal,
) {
  const query = new URLSearchParams({
    claimFilter: filters.claimFilter ?? 'ALL',
    pageSize: String(filters.pageSize ?? 50),
  })
  if (filters.state) query.set('state', filters.state)
  if (cursor) query.set('cursor', cursor)
  const response = await fetch(`${moderationBackendPath('queue')}?${query}`, {
    credentials: 'include',
    cache: 'no-store',
    signal,
  })
  return readSuccess(response, moderatorQueuePageSchema, 'Moderation queue')
}

export async function fetchModerationCase(submissionId: string, signal?: AbortSignal) {
  const response = await fetch(moderationBackendPath('case', submissionId), {
    credentials: 'include',
    cache: 'no-store',
    signal,
  })
  return readSuccess(response, moderationCaseSummarySchema, 'Moderation case')
}

export async function fetchCoverageRequests(page = 1, signal?: AbortSignal) {
  const response = await fetch(`${moderationBackendPath('coverageQueue')}?page=${page}`, {
    credentials: 'include',
    cache: 'no-store',
    signal,
  })
  return readSuccess(response, coverageQueuePageSchema, 'Coverage requests')
}

export async function fetchModerationEvidence(
  submissionId: string,
  kind: RestrictedEvidenceKind,
  findingId?: string,
  signal?: AbortSignal,
) {
  const query = findingId ? `?findingId=${encodeURIComponent(findingId)}` : ''
  const response = await fetch(
    `${moderationBackendPath('caseEvidence', submissionId, kind)}${query}`,
    { credentials: 'include', cache: 'no-store', signal },
  )
  return readSuccess(response, restrictedEvidenceResponseSchema, 'Evidence preview')
}

async function postModeration<T>(
  path: string,
  body: unknown,
  schema: { safeParse: (value: unknown) => { success: boolean; data?: T } },
  label: string,
) {
  const response = await fetch(path, {
    method: 'POST',
    credentials: 'include',
    cache: 'no-store',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
  return readSuccess(response, schema, label)
}

export function postCoverageAssessment(requestId: string, body: CoverageAssessmentRequest) {
  return postModeration(
    moderationBackendPath('coverageAssessment', requestId),
    body,
    coverageAssessmentResponseSchema,
    'Coverage assessment',
  )
}

export function useModerationQueue(filters: ModerationQueueFilters = {}) {
  return useInfiniteQuery({
    queryKey: moderationKeys.queue(filters),
    queryFn: ({ pageParam, signal }) => fetchModerationQueue(filters, pageParam, signal),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (lastPage) => lastPage.nextCursor ?? undefined,
    staleTime: 10_000,
    refetchInterval: 30_000,
  })
}

export function useModerationCase(submissionId: string) {
  return useQuery({
    queryKey: moderationKeys.case(submissionId),
    queryFn: ({ signal }) => fetchModerationCase(submissionId, signal),
    enabled: Boolean(submissionId),
    staleTime: 10_000,
    refetchInterval: false,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  })
}

export function useCoverageRequests() {
  return useInfiniteQuery({
    queryKey: moderationKeys.coverageRoot,
    queryFn: ({ pageParam, signal }) => fetchCoverageRequests(pageParam, signal),
    initialPageParam: 1,
    getNextPageParam: (lastPage) =>
      lastPage.page < lastPage.totalPages ? lastPage.page + 1 : undefined,
    staleTime: 10_000,
  })
}

export function useAssessCoverage() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (input: { requestId: string; body: CoverageAssessmentRequest }) =>
      postCoverageAssessment(input.requestId, input.body),
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: moderationKeys.coverageRoot }),
        queryClient.invalidateQueries({ queryKey: moderationKeys.all }),
      ])
    },
  })
}

export function moderationErrorMessage(error: Error | null, action?: 'claim' | 'release') {
  if (!(error instanceof ModerationApiError)) return error?.message ?? null
  if (error.status === 409 && action) {
    return action === 'claim'
      ? 'Claim state changed. Refresh the queue before trying again.'
      : 'Release state changed. Refresh the queue before trying again.'
  }
  if (error.code === 'ERR_EVIDENCE_SECURITY_RESTRICTED')
    return 'Evidence preview is unavailable while this version is security-restricted.'
  if (
    error.code === 'ERR_EVIDENCE_AUDIT_UNAVAILABLE' ||
    error.code === 'ERR_REVIEW_AUDIT_UNAVAILABLE'
  )
    return 'Evidence access could not be audited. No content was released. Try again later.'
  if (
    error.code === 'ERR_COVERAGE_REVIEW_CONFLICT' ||
    error.code === 'ERR_COVERAGE_ASSESSMENT_INVALIDATED'
  )
    return 'Coverage review changed. Refresh the request before trying again.'
  if (error.code === 'ERR_COVERAGE_SCOPE_UNAVAILABLE')
    return 'Readable evidence for the complete omitted scope is unavailable. Record a blocked assessment.'
  if (error.code === 'ERR_MODERATION_NOT_HOLDER')
    return 'Claim this case before submitting a decision.'
  if (error.code === 'ERR_MODERATION_CASE_HELD')
    return 'Another reviewer holds this case. Refresh the queue before trying again.'
  if (error.code === 'ERR_MODERATION_CASE_NOT_CLAIMED')
    return 'This case is not currently claimed. Refresh the queue before retrying.'
  if (error.code === 'ERR_MODERATION_OPERATION_CONFLICT')
    return 'This operation ID was already used with different content. Review the case and submit a new attempt.'
  if (error.code === 'ERR_MODERATION_CASE_NOT_ACTIVE')
    return 'This case is no longer active. The current case has been refreshed.'
  if (
    error.code === 'ERR_MODERATION_CASE_STALE' ||
    error.code === 'ERR_MODERATION_CONCURRENT_UPDATE'
  )
    return 'The case changed. The current case has been refreshed; your text is preserved.'
  if (error.code === 'ERR_MODERATION_DECISION_RESPONSES_OUTSTANDING')
    return 'Seller responses to required findings are outstanding.'
  if (error.code === 'ERR_MODERATION_APPEAL_DECISION_NOT_ALLOWED')
    return 'This decision is not allowed for an appeal case.'
  if (error.code === 'ERR_MODERATION_APPEAL_REVIEWER_EXCLUDED')
    return 'This reviewer is excluded from the appeal case.'
  if (error.code === 'ERR_MODERATION_APPEAL_BASIS_CHANGED')
    return 'The appeal basis changed. Refresh the case before continuing.'
  if (error.code === 'ERR_MODERATION_APPEAL_EXISTS')
    return 'An appeal already exists for this decision.'
  if (error.code === 'ERR_MODERATION_DECISION_BLOCKED')
    return 'The backend blocked this decision. Follow its response before retrying.'
  if (error.code === 'ERR_MODERATION_DISPOSITIONS_INVALID')
    return 'Provide one valid disposition for every material finding.'
  if (error.code === 'ERR_MODERATION_DECISION_EXISTS')
    return 'This submission already has a decision. The case has been refreshed.'
  if (error.code === 'ERR_COVERAGE_ASSESSMENT_BLOCKED')
    return 'The backend could not establish a safe manual review scope. Record a blocked assessment.'
  if (error.code === 'ERR_COVERAGE_ASSESSOR_UNAVAILABLE')
    return 'Coverage review needs a new reviewer. Refresh the request before continuing.'
  if (error.code === 'ERR_REVIEW_EVIDENCE_NOT_FOUND')
    return 'The requested review evidence was not found.'
  if (error.code === 'ERR_REVIEW_COVERAGE_REQUIRED')
    return 'An accepted coverage assessment is required before this decision.'
  if (error.code === 'ERR_REVIEW_PLAN_REFERENCE_MISMATCH')
    return 'The completed plan reference must match the accepted assessment after trimming.'
  if (error.code === 'ERR_ANALYSIS_NOT_AVAILABLE')
    return 'Required analysis or seller-response status is unavailable.'
  if (error.code === 'ERR_PUBLICATION_METADATA_UNAVAILABLE')
    return 'Required publication metadata is unavailable. No decision was saved.'
  if (error.code === 'ERR_MODERATION_CASE_NOT_FOUND') return 'This moderation case was not found.'
  if (error.status === 409)
    return 'Case changed. The current case has been refreshed; your text is preserved.'
  if (error.status === 403) return 'You are not permitted to perform this moderation action.'
  if (error.status === 503) return 'The review service is temporarily unavailable. Try again later.'
  return error.message
}

export function useChangeModerationClaim() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (input: {
      submissionId: string
      action: 'claim' | 'release'
      body: ClaimChangeRequest
    }) => {
      const path = moderationBackendPath(input.action, input.submissionId)
      return postModeration(path, input.body, claimChangeResponseSchema, 'Moderation claim')
    },
    onSuccess: async (saved) => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: moderationKeys.all }),
        queryClient.invalidateQueries({ queryKey: moderationKeys.case(saved.submissionId) }),
      ])
    },
  })
}

export function useSubmitModerationDecision() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (input: { submissionId: string; body: ModerationDecisionRequest }) =>
      postModeration(
        moderationBackendPath('decision', input.submissionId),
        input.body,
        moderationDecisionResponseSchema,
        'Moderation decision',
      ),
    onSuccess: async (_saved, input) => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: moderationKeys.all }),
        queryClient.invalidateQueries({ queryKey: moderationKeys.case(input.submissionId) }),
      ])
    },
  })
}
