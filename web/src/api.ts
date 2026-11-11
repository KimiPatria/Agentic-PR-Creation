// Types and calls for the PR Agent API. Field names match the API's JSON exactly.

export type BudgetType = 'Opex' | 'Capex'
export type ItemType = 'Material' | 'Service'
export type CostObjectType = 'CostCenter' | 'Wbs'
export type RequestStatus = 'Draft' | 'Submitted' | 'SentBack' | 'Approved' | 'Creating' | 'Created' | 'CreationFailed'
export type Severity = 'Info' | 'Warning' | 'Error'

/** What the requestor enters for a line. The SAP backend fields are not here: the rules engine sets them. */
export interface LineInput {
  lineNo: number
  budgetType?: BudgetType | null
  itemType?: ItemType | null
  costObjectType?: CostObjectType | null
  isStock?: boolean | null
  materialNumber?: string | null
  shortText?: string | null
  materialGroup?: string | null
  quantity?: number | null
  uom?: string | null
  unitPrice?: number | null
  deliveryDate?: string | null
  costCenter?: string | null
  wbs?: string | null
  assetNumber?: string | null
}

export interface ResolvedLine extends LineInput {
  purchasingGroup?: string | null
  prDocType?: string | null
  acctAssignmentCategory?: string | null
  itemCategory?: string | null
  glAccount?: string | null
  total?: number
  splitId?: string | null
}

export interface DraftInput {
  title: string
  justification: string
  plant: string
  currency: string
  sectionId: string | null
  lines: LineInput[]
}

export interface ValidationMessage {
  severity: Severity
  field: string | null
  text: string
  lineNo: number | null
}

export interface LineEvaluation {
  lineNo: number
  resolved: ResolvedLine
  lockedFields: string[]
  lockReasons: Record<string, string>
  requiredFields: string[]
  allowedOptions: Record<string, string[]>
  messages: ValidationMessage[]
  matchedRuleCode: string | null
  matchedRuleIsPlaceholder: boolean
  isValid: boolean
}

export interface SplitGroup {
  key: string
  label: string
  lineNos: number[]
  total: number
  prDocType: string | null
  purchasingGroup: string | null
}

export interface BudgetCheck {
  objectType: CostObjectType
  objectCode: string
  currency: string
  requested: number
  remaining: number | null
  isOver: boolean
  text: string
  lineNos: number[]
}

export interface Evaluation {
  lines: LineEvaluation[]
  messages: ValidationMessage[]
  splits: SplitGroup[]
  budget: BudgetCheck[]
  canSubmit: boolean
  usesPlaceholderRules: boolean
}

export interface PrSplit {
  id: string
  sequence: number
  label: string
  status: 'Pending' | 'Created' | 'Failed'
  sapPrNumber: string | null
  isSimulated: boolean
  attempts: number
  lastError: string | null
}

export interface Attachment {
  id: string
  fileName: string
  contentType: string
  sizeBytes: number
  uploadedBy: string
  uploadedAt: string
}

export interface RequestDto {
  id: string
  reference: string
  status: RequestStatus
  title: string
  justification: string
  plant: string
  currency: string
  sectionId: string | null
  sectionName: string | null
  requestorName: string
  requestorEmail: string
  reviewerName: string | null
  reviewComment: string | null
  createdAt: string
  updatedAt: string
  submittedAt: string | null
  decidedAt: string | null
  total: number
  lines: ResolvedLine[]
  splits: PrSplit[]
  attachments: Attachment[]
  permissions: { canEdit: boolean; canSubmit: boolean; canReview: boolean; canRetry: boolean }
}

export interface RequestWithEvaluation {
  request: RequestDto
  evaluation: Evaluation
}

export interface RequestSummary {
  id: string
  reference: string
  status: RequestStatus
  title: string
  requestorName: string
  sectionName: string | null
  total: number
  currency: string
  lineCount: number
  updatedAt: string
  submittedAt: string | null
  prNumbers: string[]
  isSimulated: boolean
}

export interface Option {
  code: string
  name: string
  meta?: Record<string, unknown> | null
}

export interface Section {
  id: string
  code: string
  name: string
  hasReviewer: boolean
}

export interface Me {
  id: string
  email: string
  name: string
  roles: string[]
  sectionId: string | null
  reviewsSectionIds: string[]
}

export interface PublicConfig {
  authMode: 'Dev' | 'Cognito'
  sapMode: string
  devUsers: { email: string; name: string; roles: string[] }[] | null
}

export interface Meta {
  sapMode: string
  sapSimulated: boolean
  splitKeys: string[]
}

export interface HistoryEvent {
  id: number
  at: string
  actorEmail: string
  action: string
  data: string | null
}

export interface Rule {
  id: string
  code: string
  description: string
  conditions: Record<string, string>
  priority: number
  allowed: boolean
  message: string | null
  prDocType: string | null
  acctAssignmentCategory: string | null
  itemCategory: string | null
  glMode: 'None' | 'FromMaterialGroup' | 'Fixed'
  glFixedAccount: string | null
  requiredFields: string[]
  isPlaceholder: boolean
  isActive: boolean
  validFrom: string
  validTo: string | null
  updatedAt?: string
  updatedBy?: string
}

export interface AdminSection {
  id: string
  code: string
  name: string
  reviewers: { email: string; name: string }[]
}

export interface MaterialGroup {
  code: string
  name: string
  itemType: ItemType | null
  defaultGlAccount: string | null
  defaultPurchasingGroup: string | null
}

export interface Job {
  id: string
  type: string
  status: 'Pending' | 'Processed' | 'Failed'
  attempts: number
  createdAt: string
  nextAttemptAt: string
  processedAt: string | null
  lastError: string | null
  requestId: string | null
  requestReference: string | null
  summary: string
}

export interface AuditEvent {
  id: number
  at: string
  actorEmail: string
  action: string
  entityType: string
  entityId: string
  data: string | null
}

export interface SapStatus {
  mode: string
  simulated: boolean
  cacheInvalidatedAt: string | null
  simulatedPrs: { prNumber: string; request: Record<string, unknown>; attachments: string[] }[] | null
}

/** A failed API call. `message` is written for the user; `evaluation` is present on validation failures. */
export class ApiError extends Error {
  readonly status: number
  readonly evaluation?: Evaluation

  constructor(status: number, message: string, evaluation?: Evaluation) {
    super(message)
    this.status = status
    this.evaluation = evaluation
  }
}

type HeaderSource = () => Record<string, string>
let authHeaders: HeaderSource = () => ({})

/** Called by the auth layer so every request carries the current identity. */
export function setAuthHeaders(source: HeaderSource): void {
  authHeaders = source
}

export async function rawFetch(path: string, init: RequestInit = {}): Promise<Response> {
  const headers = new Headers(init.headers)
  for (const [key, value] of Object.entries(authHeaders())) headers.set(key, value)
  if (init.body && !(init.body instanceof FormData) && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json')

  let response: Response
  try {
    response = await fetch(path, { ...init, headers })
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw error
    throw new ApiError(0, 'The server cannot be reached. Check that the API is running.')
  }
  if (response.ok) return response

  let detail = `The server answered ${response.status}.`
  let evaluation: Evaluation | undefined
  try {
    const problem = await response.json()
    detail = problem.detail ?? problem.title ?? detail
    evaluation = problem.evaluation
  } catch {
    /* no JSON body */
  }
  if (response.status === 401) detail = 'You are not signed in, or your session has ended.'
  throw new ApiError(response.status, detail, evaluation)
}

async function call<T>(path: string, method = 'GET', body?: unknown): Promise<T> {
  const response = await rawFetch(path, { method, body: body === undefined ? undefined : JSON.stringify(body) })
  return response.status === 204 ? (undefined as T) : ((await response.json()) as T)
}

export const api = {
  publicConfig: () => call<PublicConfig>('/api/public/config'),
  me: () => call<Me>('/api/me'),
  meta: () => call<Meta>('/api/meta'),
  sections: () => call<Section[]>('/api/sections'),
  masterData: (kind: string, query = '', max = 50) => call<Option[]>(`/api/master-data/${kind}?q=${encodeURIComponent(query)}&max=${max}`),
  evaluate: (draft: DraftInput) => call<Evaluation>('/api/rules/evaluate', 'POST', draft),

  requests: (scope: 'mine' | 'review' | 'all', status?: RequestStatus) =>
    call<RequestSummary[]>(`/api/requests?scope=${scope}${status ? `&status=${status}` : ''}`),
  request: (id: string) => call<RequestWithEvaluation>(`/api/requests/${id}`),
  createRequest: (draft: DraftInput) => call<RequestWithEvaluation>('/api/requests', 'POST', draft),
  updateRequest: (id: string, draft: DraftInput) => call<RequestWithEvaluation>(`/api/requests/${id}`, 'PUT', draft),
  deleteRequest: (id: string) => call<void>(`/api/requests/${id}`, 'DELETE'),
  submit: (id: string) => call<RequestWithEvaluation>(`/api/requests/${id}/submit`, 'POST', {}),
  approve: (id: string, comment: string) => call<RequestWithEvaluation>(`/api/requests/${id}/approve`, 'POST', { comment }),
  sendBack: (id: string, comment: string) => call<RequestWithEvaluation>(`/api/requests/${id}/send-back`, 'POST', { comment }),
  retry: (id: string) => call<RequestWithEvaluation>(`/api/requests/${id}/retry`, 'POST', {}),
  history: (id: string) => call<HistoryEvent[]>(`/api/requests/${id}/history`),

  uploadAttachment: async (id: string, file: File) => {
    const form = new FormData()
    form.append('file', file)
    return (await (await rawFetch(`/api/requests/${id}/attachments`, { method: 'POST', body: form })).json()) as Attachment
  },
  removeAttachment: (id: string, attachmentId: string) => call<void>(`/api/requests/${id}/attachments/${attachmentId}`, 'DELETE'),
  downloadAttachment: async (id: string, attachment: Attachment) => {
    const blob = await (await rawFetch(`/api/requests/${id}/attachments/${attachment.id}`)).blob()
    const url = URL.createObjectURL(blob)
    const link = document.createElement('a')
    link.href = url
    link.download = attachment.fileName
    link.click()
    URL.revokeObjectURL(url)
  },

  saveSuggestionOutcome: (body: { sessionId: string; requestId: string | null; proposal: unknown; outcome: unknown }) =>
    call<void>('/api/agent/suggestions', 'POST', body),

  admin: {
    rules: () => call<{ rules: Rule[]; factDomains: Record<string, string[]>; requirableFields: string[] }>('/api/admin/rules'),
    saveRule: (rule: Rule) => (rule.id ? call<Rule>(`/api/admin/rules/${rule.id}`, 'PUT', rule) : call<Rule>('/api/admin/rules', 'POST', rule)),
    deleteRule: (id: string) => call<void>(`/api/admin/rules/${id}`, 'DELETE'),
    sections: () => call<AdminSection[]>('/api/admin/sections'),
    saveSection: (section: AdminSection) =>
      section.id ? call<AdminSection>(`/api/admin/sections/${section.id}`, 'PUT', section) : call<AdminSection>('/api/admin/sections', 'POST', section),
    deleteSection: (id: string) => call<void>(`/api/admin/sections/${id}`, 'DELETE'),
    materialGroups: () => call<MaterialGroup[]>('/api/admin/material-groups'),
    saveMaterialGroup: (group: MaterialGroup) => call<MaterialGroup>(`/api/admin/material-groups/${group.code}`, 'PUT', group),
    jobs: () => call<Job[]>('/api/admin/jobs'),
    retryJob: (id: string) => call<Job>(`/api/admin/jobs/${id}/retry`, 'POST', {}),
    audit: () => call<AuditEvent[]>('/api/admin/audit?take=200'),
    sap: () => call<SapStatus>('/api/admin/sap'),
    refreshMasterData: () => call<{ invalidatedAt: string }>('/api/admin/master-data/refresh', 'POST', {}),
  },
}

// ---- Agent chat ------------------------------------------------------------------------------

export interface Proposal {
  id: string
  title: string
  justification: string
  currency: string
  explanation: string
  lines: LineInput[]
  reasons: Record<string, string>
  check: unknown
}

export type AgentEvent =
  | { type: 'text'; delta: string }
  | { type: 'tool'; name: string }
  | { type: 'proposal'; proposal: Proposal }
  | { type: 'error'; message: string }
  | { type: 'done' }

/** Sends one message to the assistant and calls `onEvent` for every event of the reply stream. */
export async function chat(
  body: { sessionId: string; message: string; form: DraftInput; requestId: string | null },
  onEvent: (event: AgentEvent) => void,
  signal?: AbortSignal,
): Promise<void> {
  const response = await rawFetch('/api/agent/chat', { method: 'POST', body: JSON.stringify(body), signal })
  const reader = response.body!.getReader()
  const decoder = new TextDecoder()
  let buffer = ''

  for (;;) {
    const { done, value } = await reader.read()
    if (done) break
    buffer += decoder.decode(value, { stream: true })

    // Server-sent events are separated by a blank line; each carries one JSON object after "data:".
    let boundary: number
    while ((boundary = buffer.search(/\r?\n\r?\n/)) >= 0) {
      const block = buffer.slice(0, boundary)
      buffer = buffer.slice(boundary).replace(/^\r?\n\r?\n/, '')
      const data = block
        .split(/\r?\n/)
        .filter((line) => line.startsWith('data:'))
        .map((line) => line.slice(5).trim())
        .join('')
      if (!data) continue
      try {
        onEvent(JSON.parse(data) as AgentEvent)
      } catch {
        /* ignore a malformed event rather than end the conversation */
      }
    }
  }
}
