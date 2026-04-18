import { useEffect, useMemo, useRef, useState } from 'react'
import ApiService, {
  AdminUserSummary,
  BatchCoachingJobCreateRequest,
  BatchCoachingJobCreateResponse,
  BatchCoachingJobDetails,
  BatchCoachingJobItem,
  BatchCoachingJobItemStatus,
  BatchCoachingJobItemsResponse,
  BatchCoachingJobStatus,
  BatchCoachingJobSummary,
  RetentionRunSummary,
  RetentionStatusResponse
} from '../services/ApiService'
import '../styles/pages.css'
import '../styles/admin-batch.css'

type RoleValue = 'User' | 'Admin'
type JobStatusFilter = BatchCoachingJobItemStatus | 'All'

const ACTIVE_JOB_STATUSES = new Set<BatchCoachingJobStatus | string>(['Queued', 'Running'])

function AdminPage() {
  const [status, setStatus] = useState<RetentionStatusResponse | null>(null)
  const [users, setUsers] = useState<AdminUserSummary[]>([])

  const [loadingStatus, setLoadingStatus] = useState(true)
  const [runningRetention, setRunningRetention] = useState(false)
  const [loadingUsers, setLoadingUsers] = useState(true)

  const [savingRoleUserId, setSavingRoleUserId] = useState<string | null>(null)

  const [statusMessage, setStatusMessage] = useState<string | null>(null)
  const [usersMessage, setUsersMessage] = useState<string | null>(null)
  const [errorMessage, setErrorMessage] = useState<string | null>(null)

  const [roleDrafts, setRoleDrafts] = useState<Record<string, RoleValue>>({})

  const [sessionIdsInput, setSessionIdsInput] = useState('')
  const [filterCreatedFromUtc, setFilterCreatedFromUtc] = useState('')
  const [filterCreatedToUtc, setFilterCreatedToUtc] = useState('')
  const [filterLanguage, setFilterLanguage] = useState('')
  const [filterRoleContains, setFilterRoleContains] = useState('')
  const [filterOnlyIfNoCoach, setFilterOnlyIfNoCoach] = useState(true)
  const [optionForce, setOptionForce] = useState(false)
  const [optionMaxSessions, setOptionMaxSessions] = useState('100')
  const [optionParallelism, setOptionParallelism] = useState('2')
  const [optionStopOnError, setOptionStopOnError] = useState(false)

  const [creatingBatchJob, setCreatingBatchJob] = useState(false)
  const [batchJobsLoading, setBatchJobsLoading] = useState(true)
  const [batchJobs, setBatchJobs] = useState<BatchCoachingJobSummary[]>([])
  const [selectedJobId, setSelectedJobId] = useState<string | null>(null)
  const [selectedJobDetails, setSelectedJobDetails] = useState<BatchCoachingJobDetails | null>(null)
  const [selectedJobLoading, setSelectedJobLoading] = useState(false)
  const [selectedJobItemsLoading, setSelectedJobItemsLoading] = useState(false)
  const [batchItems, setBatchItems] = useState<BatchCoachingJobItem[]>([])
  const [batchItemsTotal, setBatchItemsTotal] = useState<number | null>(null)
  const [batchItemsStatusFilter, setBatchItemsStatusFilter] = useState<JobStatusFilter>('All')
  const [batchItemsTake, setBatchItemsTake] = useState(25)
  const [batchItemsSkip, setBatchItemsSkip] = useState(0)
  const [cancelingJob, setCancelingJob] = useState(false)
  const [batchMessage, setBatchMessage] = useState<string | null>(null)
  const [batchError, setBatchError] = useState<string | null>(null)

  const jobsListPollInFlightRef = useRef(false)
  const selectedJobPollInFlightRef = useRef(false)
  const selectedItemsPollInFlightRef = useRef(false)

  const parseErrorMessage = (error: any): string => {
    const statusCode = error?.response?.status
    const detail = error?.response?.data?.detail
    const title = error?.response?.data?.title

    if (typeof detail === 'string' && detail.length > 0) {
      return detail
    }

    if (typeof title === 'string' && title.length > 0) {
      return title
    }

    if (statusCode === 403) {
      return 'Admin access required.'
    }

    if (statusCode === 409) {
      return 'Operation conflict.'
    }

    if (statusCode === 404) {
      return 'Requested record was not found.'
    }

    return 'Operation failed. Please try again.'
  }

  const parseOptionalJsonObject = (value: unknown): Record<string, unknown> | null => {
    if (!value) {
      return null
    }

    if (typeof value === 'string') {
      try {
        const parsed = JSON.parse(value)
        return typeof parsed === 'object' && parsed !== null ? parsed as Record<string, unknown> : null
      } catch {
        return null
      }
    }

    if (typeof value === 'object') {
      return value as Record<string, unknown>
    }

    return null
  }

  const normalizeJobSummary = (input: any): BatchCoachingJobSummary => {
    const totalSessions = Number(input?.totalSessions ?? 0)
    const processedSessions = Number(input?.processedSessions ?? 0)
    const computedProgress = totalSessions > 0 ? (processedSessions / totalSessions) * 100 : 0

    return {
      jobId: String(input?.jobId ?? input?.id ?? ''),
      status: input?.status ?? 'Queued',
      createdAtUtc: input?.createdAtUtc ?? input?.createdAt,
      startedAtUtc: input?.startedAtUtc ?? null,
      completedAtUtc: input?.completedAtUtc ?? null,
      totalSessions,
      processedSessions,
      succeededSessions: Number(input?.succeededSessions ?? 0),
      failedSessions: Number(input?.failedSessions ?? 0),
      skippedSessions: Number(input?.skippedSessions ?? 0),
      progressPercent: typeof input?.progressPercent === 'number'
        ? input.progressPercent
        : computedProgress,
      lastError: input?.lastError ?? null
    }
  }

  const normalizeJobDetails = (input: any): BatchCoachingJobDetails => {
    const summary = normalizeJobSummary(input)
    const filters = parseOptionalJsonObject(input?.filters ?? input?.filtersJson)
    const options = parseOptionalJsonObject(input?.options ?? input?.optionsJson)

    return {
      ...summary,
      createdByUserId: input?.createdByUserId ?? null,
      filters: filters as BatchCoachingJobDetails['filters'],
      options: options as BatchCoachingJobDetails['options'],
      filtersJson: typeof input?.filtersJson === 'string' ? input.filtersJson : null,
      optionsJson: typeof input?.optionsJson === 'string' ? input.optionsJson : null
    }
  }

  const normalizeJobItem = (input: any): BatchCoachingJobItem => ({
    id: input?.id ? String(input.id) : undefined,
    jobId: input?.jobId ? String(input.jobId) : undefined,
    sessionId: String(input?.sessionId ?? ''),
    status: input?.status ?? 'Pending',
    attempts: Number(input?.attempts ?? 0),
    startedAtUtc: input?.startedAtUtc ?? null,
    completedAtUtc: input?.completedAtUtc ?? null,
    resultSource: input?.resultSource ?? null,
    llmRunId: input?.llmRunId ?? null,
    error: input?.error ?? null
  })

  const normalizeItemsResponse = (response: BatchCoachingJobItemsResponse | BatchCoachingJobItem[] | any): BatchCoachingJobItemsResponse => {
    if (Array.isArray(response)) {
      return {
        items: response.map((item) => normalizeJobItem(item))
      }
    }

    if (response && Array.isArray(response.items)) {
      return {
        items: response.items.map((item: any) => normalizeJobItem(item)),
        totalCount: typeof response.totalCount === 'number' ? response.totalCount : undefined,
        take: typeof response.take === 'number' ? response.take : undefined,
        skip: typeof response.skip === 'number' ? response.skip : undefined
      }
    }

    return { items: [] }
  }

  const parseSessionIds = (): string[] => {
    return Array.from(
      new Set(
        sessionIdsInput
          .split(/[\n,]+/g)
          .map((id) => id.trim())
          .filter((id) => id.length > 0)
      )
    )
  }

  const hasAtLeastOneFilter = (): boolean => {
    return (
      filterCreatedFromUtc.trim().length > 0 ||
      filterCreatedToUtc.trim().length > 0 ||
      filterLanguage.trim().length > 0 ||
      filterRoleContains.trim().length > 0 ||
      filterOnlyIfNoCoach
    )
  }

  const buildCreateRequest = (): BatchCoachingJobCreateRequest | null => {
    const normalizedSessionIds = parseSessionIds()
    const useSessionIds = normalizedSessionIds.length > 0

    const filters = {
      createdFromUtc: filterCreatedFromUtc.trim() || undefined,
      createdToUtc: filterCreatedToUtc.trim() || undefined,
      language: filterLanguage.trim() || undefined,
      roleContains: filterRoleContains.trim() || undefined,
      onlyIfNoCoach: filterOnlyIfNoCoach
    }

    const options = {
      force: optionForce,
      maxSessions: Number(optionMaxSessions || 0) || undefined,
      parallelism: Number(optionParallelism || 0) || undefined,
      stopOnError: optionStopOnError
    }

    if (!useSessionIds && !hasAtLeastOneFilter()) {
      return null
    }

    if (!useSessionIds && Object.values(filters).every((value) => value === undefined || value === false || value === '')) {
      return null
    }

    return {
      sessionIds: useSessionIds ? normalizedSessionIds : undefined,
      filters: useSessionIds ? undefined : filters,
      options
    }
  }

  const formatDate = (value?: string | null): string => {
    if (!value) {
      return '-'
    }

    const date = new Date(value)
    if (Number.isNaN(date.getTime())) {
      return value
    }

    return date.toLocaleString()
  }

  const toShortId = (id: string): string => {
    if (!id || id.length < 12) {
      return id
    }

    return `${id.slice(0, 8)}...${id.slice(-4)}`
  }

  const parseDetailsFromCancelResponse = (response: BatchCoachingJobDetails): BatchCoachingJobDetails => {
    return normalizeJobDetails(response)
  }

  const loadStatus = async () => {
    try {
      const nextStatus = await ApiService.getRetentionStatus()
      setStatus(nextStatus)
    } catch (error) {
      setErrorMessage(parseErrorMessage(error))
      setStatus(null)
    } finally {
      setLoadingStatus(false)
    }
  }

  const loadUsers = async () => {
    try {
      const list = await ApiService.getAdminUsers(100)
      setUsers(list)
      setRoleDrafts(Object.fromEntries(list.map((u) => [u.userId, u.role as RoleValue])))
    } catch (error) {
      setErrorMessage(parseErrorMessage(error))
      setUsers([])
    } finally {
      setLoadingUsers(false)
    }
  }

  const loadAll = async () => {
    setErrorMessage(null)
    setLoadingStatus(true)
    setLoadingUsers(true)
    await Promise.all([loadStatus(), loadUsers()])
  }

  const loadBatchJobs = async (withLoadingState: boolean) => {
    if (withLoadingState) {
      setBatchJobsLoading(true)
    }

    try {
      const list = await ApiService.getBatchCoachJobs(20)
      const normalized = list.map((job) => normalizeJobSummary(job))
      setBatchJobs(normalized)

      if (!selectedJobId && normalized.length > 0) {
        setSelectedJobId(normalized[0].jobId)
      }
    } catch (error) {
      setBatchError(parseErrorMessage(error))
      if (withLoadingState) {
        setBatchJobs([])
      }
    } finally {
      if (withLoadingState) {
        setBatchJobsLoading(false)
      }
    }
  }

  const loadSelectedJob = async (jobId: string, withLoadingState: boolean) => {
    if (withLoadingState) {
      setSelectedJobLoading(true)
    }

    try {
      const details = await ApiService.getBatchCoachJob(jobId)
      setSelectedJobDetails(normalizeJobDetails(details))
    } catch (error) {
      setBatchError(parseErrorMessage(error))
      setSelectedJobDetails(null)
    } finally {
      if (withLoadingState) {
        setSelectedJobLoading(false)
      }
    }
  }

  const loadSelectedJobItems = async (jobId: string, withLoadingState: boolean) => {
    if (withLoadingState) {
      setSelectedJobItemsLoading(true)
    }

    try {
      const response = await ApiService.getBatchCoachJobItems(jobId, {
        status: batchItemsStatusFilter === 'All' ? undefined : batchItemsStatusFilter,
        take: batchItemsTake,
        skip: batchItemsSkip
      })
      const normalized = normalizeItemsResponse(response)
      setBatchItems(normalized.items)
      setBatchItemsTotal(normalized.totalCount ?? null)
    } catch (error) {
      setBatchError(parseErrorMessage(error))
      setBatchItems([])
      setBatchItemsTotal(null)
    } finally {
      if (withLoadingState) {
        setSelectedJobItemsLoading(false)
      }
    }
  }

  useEffect(() => {
    void loadAll()
    void loadBatchJobs(true)
  }, [])

  useEffect(() => {
    if (!selectedJobId) {
      return
    }

    setBatchItemsSkip(0)
    void loadSelectedJob(selectedJobId, true)
    void loadSelectedJobItems(selectedJobId, true)
  }, [selectedJobId])

  useEffect(() => {
    if (!selectedJobId) {
      return
    }

    setBatchItemsSkip(0)
    void loadSelectedJobItems(selectedJobId, true)
  }, [batchItemsStatusFilter, batchItemsTake])

  useEffect(() => {
    if (!selectedJobId) {
      return
    }

    void loadSelectedJobItems(selectedJobId, true)
  }, [batchItemsSkip])

  useEffect(() => {
    const hasActiveJob = batchJobs.some((job) => ACTIVE_JOB_STATUSES.has(job.status ?? ''))
    if (!hasActiveJob) {
      return
    }

    const timer = window.setInterval(() => {
      if (jobsListPollInFlightRef.current) {
        return
      }

      jobsListPollInFlightRef.current = true
      void loadBatchJobs(false).finally(() => {
        jobsListPollInFlightRef.current = false
      })
    }, 5000)

    return () => window.clearInterval(timer)
  }, [batchJobs])

  useEffect(() => {
    if (!selectedJobId || !selectedJobDetails || !ACTIVE_JOB_STATUSES.has(selectedJobDetails.status ?? '')) {
      return
    }

    const timer = window.setInterval(() => {
      if (selectedJobPollInFlightRef.current || selectedItemsPollInFlightRef.current) {
        return
      }

      selectedJobPollInFlightRef.current = true
      selectedItemsPollInFlightRef.current = true

      void Promise.all([
        loadSelectedJob(selectedJobId, false),
        loadSelectedJobItems(selectedJobId, false)
      ]).finally(() => {
        selectedJobPollInFlightRef.current = false
        selectedItemsPollInFlightRef.current = false
      })
    }, 3000)

    return () => window.clearInterval(timer)
  }, [selectedJobId, selectedJobDetails])

  const onRunRetention = async () => {
    if (runningRetention) {
      return
    }

    setRunningRetention(true)
    setStatusMessage(null)
    setErrorMessage(null)

    try {
      const summary: RetentionRunSummary = await ApiService.runRetention()
      setStatus((prev) => {
        if (!prev) {
          return {
            enabled: true,
            deleteAfterDays: 0,
            keepSummariesOnlyAfterDays: null,
            runHourUtc: 0,
            lastRun: summary
          }
        }

        return {
          ...prev,
          lastRun: summary
        }
      })
      setStatusMessage('Retention run completed.')
    } catch (error) {
      setErrorMessage(parseErrorMessage(error))
    } finally {
      setRunningRetention(false)
    }
  }

  const onSaveRole = async (userId: string) => {
    const nextRole = roleDrafts[userId]
    if (!nextRole || savingRoleUserId) {
      return
    }

    setSavingRoleUserId(userId)
    setUsersMessage(null)
    setErrorMessage(null)

    try {
      await ApiService.setUserRole(userId, nextRole)
      setUsersMessage('User role updated.')
      await loadUsers()
    } catch (error) {
      setErrorMessage(parseErrorMessage(error))
    } finally {
      setSavingRoleUserId(null)
    }
  }

  const onCreateBatchJob = async () => {
    const payload = buildCreateRequest()
    if (!payload) {
      setBatchError('Provide sessionIds or at least one filter.')
      return
    }

    setBatchError(null)
    setBatchMessage(null)
    setCreatingBatchJob(true)

    try {
      const result: BatchCoachingJobCreateResponse = await ApiService.createBatchCoachJob(payload)
      const createdJobId = result.jobId
      setBatchMessage(`Batch job created: ${toShortId(createdJobId)}`)
      setSelectedJobId(createdJobId)
      await loadBatchJobs(false)
      await loadSelectedJob(createdJobId, false)
      await loadSelectedJobItems(createdJobId, false)
    } catch (error) {
      setBatchError(parseErrorMessage(error))
    } finally {
      setCreatingBatchJob(false)
    }
  }

  const onCancelSelectedJob = async () => {
    if (!selectedJobId || cancelingJob || !selectedJobDetails) {
      return
    }

    setCancelingJob(true)
    setBatchError(null)
    setBatchMessage(null)

    try {
      const response = await ApiService.cancelBatchCoachJob(selectedJobId)
      setSelectedJobDetails(parseDetailsFromCancelResponse(response))
      setBatchMessage('Cancel request submitted.')
      await loadBatchJobs(false)
      await loadSelectedJobItems(selectedJobId, false)
    } catch (error) {
      setBatchError(parseErrorMessage(error))
    } finally {
      setCancelingJob(false)
    }
  }

  const canSave = (user: AdminUserSummary): boolean => {
    return roleDrafts[user.userId] !== undefined && roleDrafts[user.userId] !== user.role
  }

  const rows = useMemo(() => users, [users])
  const selectedJobIsActive = selectedJobDetails ? ACTIVE_JOB_STATUSES.has(selectedJobDetails.status ?? '') : false
  const selectedJobProgress = selectedJobDetails?.progressPercent ?? 0
  const canGoPrevItems = batchItemsSkip > 0
  const canGoNextItems = batchItemsTotal !== null
    ? batchItemsSkip + batchItemsTake < batchItemsTotal
    : batchItems.length >= batchItemsTake

  return (
    <div className="page" data-testid="admin-page">
      <div className="container">
        <h1>Admin</h1>
        <p className="subtitle">Retention operations, user role management, and batch coaching jobs</p>

        {statusMessage && <p className="reports-inline-success" data-testid="admin-status-message">{statusMessage}</p>}
        {usersMessage && <p className="reports-inline-success" data-testid="admin-users-message">{usersMessage}</p>}
        {errorMessage && <p className="reports-inline-error" data-testid="admin-error-message">{errorMessage}</p>}

        <section className="form" style={{ maxWidth: '100%', marginBottom: 16 }} data-testid="retention-status-section">
          <h2>Retention Status</h2>
          {loadingStatus ? (
            <p className="loading-text" style={{ padding: 0 }}>Loading retention status...</p>
          ) : status ? (
            <div style={{ display: 'grid', gap: 8 }}>
              <p><strong>Enabled:</strong> {String(status.enabled)}</p>
              <p><strong>Delete After Days:</strong> {status.deleteAfterDays}</p>
              <p><strong>Keep Summaries Only After Days:</strong> {status.keepSummariesOnlyAfterDays ?? '-'}</p>
              <p><strong>Run Hour (UTC):</strong> {status.runHourUtc}</p>
              {status.lastRun && (
                <div style={{ padding: 10, border: '1px solid var(--border)', borderRadius: 8 }}>
                  <p><strong>Last Run At:</strong> {new Date(status.lastRun.ranAtUtc).toLocaleString()}</p>
                  <p><strong>Sessions Deleted:</strong> {status.lastRun.sessionsDeleted}</p>
                  <p><strong>Sessions Pruned:</strong> {status.lastRun.sessionsPruned}</p>
                  <p><strong>Metric Events Deleted:</strong> {status.lastRun.metricEventsDeleted}</p>
                  <p><strong>Transcript Segments Deleted:</strong> {status.lastRun.transcriptSegmentsDeleted}</p>
                </div>
              )}
              <div>
                <button type="button" className="btn btn-primary" data-testid="run-retention-button" disabled={runningRetention} onClick={onRunRetention}>
                  {runningRetention ? 'Running...' : 'Run Retention Now'}
                </button>
              </div>
            </div>
          ) : (
            <p className="reports-inline-error">Admin access required.</p>
          )}
        </section>

        <section className="form" style={{ maxWidth: '100%', marginBottom: 16 }} data-testid="admin-users-section">
          <h2>Users Management</h2>
          {loadingUsers ? (
            <p className="loading-text" style={{ padding: 0 }}>Loading users...</p>
          ) : rows.length === 0 ? (
            <p>No users found.</p>
          ) : (
            <div style={{ overflowX: 'auto' }}>
              <table style={{ width: '100%', borderCollapse: 'collapse' }} data-testid="admin-users-table">
                <thead>
                  <tr>
                    <th style={{ textAlign: 'left', padding: '8px 6px' }}>Email</th>
                    <th style={{ textAlign: 'left', padding: '8px 6px' }}>Role</th>
                    <th style={{ textAlign: 'left', padding: '8px 6px' }}>IsActive</th>
                    <th style={{ textAlign: 'left', padding: '8px 6px' }}>Created At</th>
                    <th style={{ textAlign: 'left', padding: '8px 6px' }}>Action</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((user) => {
                    const busy = savingRoleUserId === user.userId
                    const draftRole = roleDrafts[user.userId] ?? (user.role as RoleValue)
                    return (
                      <tr key={user.userId} data-testid={`admin-user-row-${user.userId}`}>
                        <td style={{ padding: '8px 6px' }}>{user.email}</td>
                        <td style={{ padding: '8px 6px' }}>
                          <select
                            data-testid={`admin-role-select-${user.userId}`}
                            value={draftRole}
                            onChange={(e) => setRoleDrafts((prev) => ({ ...prev, [user.userId]: e.target.value as RoleValue }))}
                            disabled={busy}
                          >
                            <option value="User">User</option>
                            <option value="Admin">Admin</option>
                          </select>
                        </td>
                        <td style={{ padding: '8px 6px' }}>{String(user.isActive)}</td>
                        <td style={{ padding: '8px 6px' }}>{new Date(user.createdAtUtc).toLocaleString()}</td>
                        <td style={{ padding: '8px 6px' }}>
                          <button
                            type="button"
                            className="btn btn-secondary"
                            data-testid={`admin-role-save-${user.userId}`}
                            disabled={busy || !canSave(user)}
                            onClick={() => void onSaveRole(user.userId)}
                          >
                            {busy ? 'Saving...' : 'Save'}
                          </button>
                        </td>
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>
          )}
        </section>

        <section className="form admin-batch-section" style={{ maxWidth: '100%' }}>
          <h2>Batch Coaching Jobs</h2>
          {batchMessage && <p className="reports-inline-success">{batchMessage}</p>}
          {batchError && <p className="reports-inline-error">{batchError}</p>}

          <div className="admin-batch-grid">
            <div className="admin-batch-column" data-testid="batch-job-create-form">
              <h3>Create Job</h3>
              <div className="form-group">
                <label htmlFor="batch-session-ids">Session IDs (optional, one per line or comma-separated)</label>
                <textarea
                  id="batch-session-ids"
                  className="admin-batch-textarea"
                  value={sessionIdsInput}
                  onChange={(e) => setSessionIdsInput(e.target.value)}
                  placeholder="session-id-1&#10;session-id-2"
                />
              </div>

              <div className="admin-batch-subtitle">Filters (optional)</div>
              <div className="admin-batch-form-grid">
                <div className="form-group">
                  <label htmlFor="batch-created-from">Created From (UTC)</label>
                  <input id="batch-created-from" type="datetime-local" value={filterCreatedFromUtc} onChange={(e) => setFilterCreatedFromUtc(e.target.value)} />
                </div>
                <div className="form-group">
                  <label htmlFor="batch-created-to">Created To (UTC)</label>
                  <input id="batch-created-to" type="datetime-local" value={filterCreatedToUtc} onChange={(e) => setFilterCreatedToUtc(e.target.value)} />
                </div>
                <div className="form-group">
                  <label htmlFor="batch-language">Language</label>
                  <input id="batch-language" type="text" value={filterLanguage} onChange={(e) => setFilterLanguage(e.target.value)} placeholder="tr" />
                </div>
                <div className="form-group">
                  <label htmlFor="batch-role-contains">Role Contains</label>
                  <input id="batch-role-contains" type="text" value={filterRoleContains} onChange={(e) => setFilterRoleContains(e.target.value)} placeholder="backend" />
                </div>
              </div>

              <label className="admin-batch-checkbox">
                <input type="checkbox" checked={filterOnlyIfNoCoach} onChange={(e) => setFilterOnlyIfNoCoach(e.target.checked)} />
                onlyIfNoCoach
              </label>

              <div className="admin-batch-subtitle">Options</div>
              <div className="admin-batch-form-grid">
                <div className="form-group">
                  <label htmlFor="batch-max-sessions">maxSessions</label>
                  <input id="batch-max-sessions" type="number" min={1} max={1000} value={optionMaxSessions} onChange={(e) => setOptionMaxSessions(e.target.value)} />
                </div>
                <div className="form-group">
                  <label htmlFor="batch-parallelism">parallelism</label>
                  <input id="batch-parallelism" type="number" min={1} max={4} value={optionParallelism} onChange={(e) => setOptionParallelism(e.target.value)} />
                </div>
              </div>

              <div className="admin-batch-checkline">
                <label className="admin-batch-checkbox">
                  <input type="checkbox" checked={optionForce} onChange={(e) => setOptionForce(e.target.checked)} />
                  force
                </label>
                <label className="admin-batch-checkbox">
                  <input type="checkbox" checked={optionStopOnError} onChange={(e) => setOptionStopOnError(e.target.checked)} />
                  stopOnError
                </label>
              </div>

              <button
                type="button"
                className="btn btn-primary"
                onClick={() => void onCreateBatchJob()}
                disabled={creatingBatchJob}
                data-testid="batch-job-submit"
              >
                {creatingBatchJob ? 'Creating...' : 'Create Batch Job'}
              </button>
            </div>

            <div className="admin-batch-column" data-testid="batch-jobs-list">
              <div className="admin-batch-row-title">
                <h3>Recent Jobs</h3>
                <button type="button" className="btn btn-secondary" onClick={() => void loadBatchJobs(false)} disabled={batchJobsLoading}>
                  Refresh
                </button>
              </div>

              {batchJobsLoading ? (
                <p className="loading-text" style={{ padding: 0 }}>Loading jobs...</p>
              ) : batchJobs.length === 0 ? (
                <p>No batch jobs found.</p>
              ) : (
                <div className="admin-batch-jobs-list">
                  {batchJobs.map((job) => (
                    <button
                      type="button"
                      key={job.jobId}
                      className={`admin-batch-job-card ${selectedJobId === job.jobId ? 'selected' : ''}`}
                      onClick={() => setSelectedJobId(job.jobId)}
                    >
                      <div className="admin-batch-job-card-top">
                        <span className="admin-batch-id">{toShortId(job.jobId)}</span>
                        <span className={`admin-batch-status status-${String(job.status || '').toLowerCase()}`}>{job.status}</span>
                      </div>
                      <div className="admin-batch-job-card-meta">{formatDate(job.createdAtUtc)}</div>
                      <div className="admin-batch-job-card-counters">
                        <span>T:{job.totalSessions ?? 0}</span>
                        <span>P:{job.processedSessions ?? 0}</span>
                        <span>S:{job.succeededSessions ?? 0}</span>
                        <span>F:{job.failedSessions ?? 0}</span>
                        <span>K:{job.skippedSessions ?? 0}</span>
                      </div>
                      <div className="admin-batch-progress-wrap">
                        <div className="admin-batch-progress-fill" style={{ width: `${Math.max(0, Math.min(100, Number(job.progressPercent ?? 0)))}%` }} />
                      </div>
                    </button>
                  ))}
                </div>
              )}
            </div>
          </div>

          <div className="admin-batch-details" data-testid="batch-job-details">
            <div className="admin-batch-row-title">
              <h3>Selected Job Details</h3>
              <div className="admin-batch-row-actions">
                <button
                  type="button"
                  className="btn btn-secondary"
                  onClick={() => {
                    if (!selectedJobId) return
                    void Promise.all([loadSelectedJob(selectedJobId, true), loadSelectedJobItems(selectedJobId, true)])
                  }}
                  disabled={!selectedJobId || selectedJobLoading || selectedJobItemsLoading}
                >
                  Refresh
                </button>
                <button
                  type="button"
                  className="btn btn-danger"
                  onClick={() => void onCancelSelectedJob()}
                  disabled={!selectedJobId || !selectedJobIsActive || cancelingJob}
                  data-testid="batch-job-cancel"
                >
                  {cancelingJob ? 'Canceling...' : 'Cancel'}
                </button>
              </div>
            </div>

            {!selectedJobId ? (
              <p>Select a job to inspect details.</p>
            ) : selectedJobLoading && !selectedJobDetails ? (
              <p className="loading-text" style={{ padding: 0 }}>Loading job details...</p>
            ) : selectedJobDetails ? (
              <>
                <div className="admin-batch-details-grid">
                  <p><strong>Job ID:</strong> {selectedJobDetails.jobId}</p>
                  <p><strong>Status:</strong> {selectedJobDetails.status}</p>
                  <p><strong>Created:</strong> {formatDate(selectedJobDetails.createdAtUtc)}</p>
                  <p><strong>Started:</strong> {formatDate(selectedJobDetails.startedAtUtc)}</p>
                  <p><strong>Completed:</strong> {formatDate(selectedJobDetails.completedAtUtc)}</p>
                  <p><strong>Total:</strong> {selectedJobDetails.totalSessions ?? 0}</p>
                  <p><strong>Processed:</strong> {selectedJobDetails.processedSessions ?? 0}</p>
                  <p><strong>Succeeded:</strong> {selectedJobDetails.succeededSessions ?? 0}</p>
                  <p><strong>Failed:</strong> {selectedJobDetails.failedSessions ?? 0}</p>
                  <p><strong>Skipped:</strong> {selectedJobDetails.skippedSessions ?? 0}</p>
                </div>

                <div className="admin-batch-progress-wrap large">
                  <div className="admin-batch-progress-fill" style={{ width: `${Math.max(0, Math.min(100, Number(selectedJobProgress)))}%` }} />
                </div>
                <p><strong>Progress:</strong> {Number(selectedJobProgress).toFixed(1)}%</p>

                {selectedJobDetails.lastError && <p className="reports-inline-error" style={{ marginTop: 8 }}>{selectedJobDetails.lastError}</p>}

                <div className="admin-batch-json-grid">
                  <div>
                    <h4>Options</h4>
                    <pre>{JSON.stringify(selectedJobDetails.options ?? {}, null, 2)}</pre>
                  </div>
                  <div>
                    <h4>Filters</h4>
                    <pre>{JSON.stringify(selectedJobDetails.filters ?? {}, null, 2)}</pre>
                  </div>
                </div>
              </>
            ) : (
              <p>No details available.</p>
            )}

            <div className="admin-batch-items">
              <div className="admin-batch-row-title">
                <h4>Job Items</h4>
                <div className="admin-batch-row-actions">
                  <label htmlFor="batch-job-items-status">Status</label>
                  <select
                    id="batch-job-items-status"
                    value={batchItemsStatusFilter}
                    onChange={(e) => setBatchItemsStatusFilter(e.target.value as JobStatusFilter)}
                    data-testid="batch-job-items-filter-status"
                  >
                    <option value="All">All</option>
                    <option value="Pending">Pending</option>
                    <option value="Running">Running</option>
                    <option value="Succeeded">Succeeded</option>
                    <option value="Failed">Failed</option>
                    <option value="Skipped">Skipped</option>
                  </select>
                  <label htmlFor="batch-job-items-take">Take</label>
                  <select id="batch-job-items-take" value={batchItemsTake} onChange={(e) => setBatchItemsTake(Number(e.target.value))}>
                    <option value={10}>10</option>
                    <option value={25}>25</option>
                    <option value={50}>50</option>
                    <option value={100}>100</option>
                  </select>
                </div>
              </div>

              {selectedJobItemsLoading ? (
                <p className="loading-text" style={{ padding: 0 }}>Loading items...</p>
              ) : batchItems.length === 0 ? (
                <p>No items in this view.</p>
              ) : (
                <div className="admin-batch-table-wrap">
                  <table className="admin-batch-table">
                    <thead>
                      <tr>
                        <th>Session</th>
                        <th>Status</th>
                        <th>Attempts</th>
                        <th>Source</th>
                        <th>Error</th>
                        <th>Started</th>
                        <th>Completed</th>
                      </tr>
                    </thead>
                    <tbody>
                      {batchItems.map((item, index) => (
                        <tr key={`${item.sessionId}-${index}`}>
                          <td>{toShortId(item.sessionId)}</td>
                          <td>{item.status}</td>
                          <td>{item.attempts ?? 0}</td>
                          <td>{item.resultSource ?? '-'}</td>
                          <td className="admin-batch-error-cell">{item.error ?? '-'}</td>
                          <td>{formatDate(item.startedAtUtc)}</td>
                          <td>{formatDate(item.completedAtUtc)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}

              <div className="admin-batch-pagination">
                <button type="button" className="btn btn-secondary" disabled={!canGoPrevItems} onClick={() => setBatchItemsSkip((prev) => Math.max(0, prev - batchItemsTake))}>
                  Prev
                </button>
                <span>Skip: {batchItemsSkip}{batchItemsTotal !== null ? ` / Total: ${batchItemsTotal}` : ''}</span>
                <button type="button" className="btn btn-secondary" disabled={!canGoNextItems} onClick={() => setBatchItemsSkip((prev) => prev + batchItemsTake)}>
                  Next
                </button>
              </div>
            </div>
          </div>
        </section>
      </div>
    </div>
  )
}

export default AdminPage
