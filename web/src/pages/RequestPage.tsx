import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import type { DraftInput, Evaluation, LineInput, Proposal, RequestDto, RequestWithEvaluation } from '../api'
import { ApiError, api } from '../api'
import { useSession } from '../auth'
import { ChatPanel } from '../components/ChatPanel'
import { LineEditor } from '../components/LineEditor'
import { MasterSelect } from '../components/MasterData'
import { Badge, Button, Card, ErrorNote, Field, Loading, Note, SimulatedTag, StatusBadge, Table, cell, dateTime, inputClass, money, sapValue } from '../ui'

const EMPTY: DraftInput = { title: '', justification: '', plant: '', currency: 'IDR', sectionId: null, lines: [{ lineNo: 1 }] }

const INPUT_FIELDS: (keyof LineInput)[] = [
  'lineNo', 'budgetType', 'itemType', 'costObjectType', 'isStock', 'materialNumber', 'shortText', 'materialGroup',
  'quantity', 'uom', 'unitPrice', 'deliveryDate', 'costCenter', 'wbs', 'assetNumber',
]

function toDraft(request: RequestDto): DraftInput {
  return {
    title: request.title,
    justification: request.justification,
    plant: request.plant,
    currency: request.currency,
    sectionId: request.sectionId,
    lines: request.lines.map((line) => Object.fromEntries(INPUT_FIELDS.map((f) => [f, line[f] ?? null])) as unknown as LineInput),
  }
}

/** The guided PR form: new request, draft being edited, or a read-only view with review actions. */
export function RequestPage() {
  // One route serves both cases, so saving a new request (which changes the URL from /requests/new
  // to /requests/{id}) keeps this component, and the form state in it, mounted.
  const params = useParams()
  const id = params.id === 'new' ? undefined : params.id
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const { me } = useSession()

  const loaded = useQuery({
    queryKey: ['request', id],
    queryFn: () => api.request(id!),
    enabled: !!id,
    // While SAP creation is running, keep looking until it settles.
    refetchInterval: (query) => (['Approved', 'Creating'].includes(query.state.data?.request.status ?? '') ? 1500 : false),
  })
  const meta = useQuery({ queryKey: ['meta'], queryFn: api.meta, staleTime: Infinity })
  const sections = useQuery({ queryKey: ['sections'], queryFn: api.sections, staleTime: 60_000 })

  const [draft, setDraft] = useState<DraftInput>(() => ({ ...EMPTY, sectionId: me.sectionId }))
  const [evaluation, setEvaluation] = useState<Evaluation | null>(null)
  const [dirty, setDirty] = useState(false)
  const [showAllErrors, setShowAllErrors] = useState(false)
  const [actionError, setActionError] = useState<unknown>(null)
  const loadedFor = useRef<string | null>(null)

  const request = loaded.data?.request
  const editable = !id || !!request?.permissions.canEdit

  // Load the saved draft into the form once per request (not on every background refetch).
  useEffect(() => {
    if (loaded.data && loadedFor.current !== loaded.data.request.id) {
      loadedFor.current = loaded.data.request.id
      setDraft(toDraft(loaded.data.request))
      setEvaluation(loaded.data.evaluation)
      setDirty(false)
    }
  }, [loaded.data])

  // Re-evaluate shortly after every change: this is what makes the dropdowns cascade and the SAP fields fill in.
  const evaluationRun = useRef(0)
  useEffect(() => {
    if (!editable || (id && !dirty)) return
    const run = ++evaluationRun.current
    const timer = setTimeout(() => {
      api
        .evaluate(draft)
        .then((result) => run === evaluationRun.current && setEvaluation(result))
        .catch(() => undefined) // the next change tries again; saving reports real errors
    }, 300)
    return () => clearTimeout(timer)
  }, [draft, editable, id, dirty])

  const change = (patch: Partial<DraftInput>) => {
    setDraft((current) => ({ ...current, ...patch }))
    setDirty(true)
  }
  const changeLine = (lineNo: number, patch: Partial<LineInput>) =>
    change({ lines: draft.lines.map((line) => (line.lineNo === lineNo ? { ...line, ...patch } : line)) })
  const removeLine = (lineNo: number) =>
    change({ lines: draft.lines.filter((line) => line.lineNo !== lineNo).map((line, index) => ({ ...line, lineNo: index + 1 })) })
  const addLine = () => {
    const last = draft.lines[draft.lines.length - 1]
    // A new line starts with the previous line's budget and cost object: most requests charge one place.
    change({
      lines: [
        ...draft.lines,
        { lineNo: draft.lines.length + 1, budgetType: last?.budgetType, costObjectType: last?.costObjectType, costCenter: last?.costCenter, wbs: last?.wbs, deliveryDate: last?.deliveryDate },
      ],
    })
  }

  const applyProposal = (proposal: Proposal) =>
    change({
      title: draft.title || proposal.title,
      justification: draft.justification || proposal.justification,
      currency: proposal.currency || draft.currency,
      lines: proposal.lines.map((line, index) => ({ ...line, lineNo: index + 1 })),
    })

  const accept = (result: RequestWithEvaluation) => {
    queryClient.setQueryData(['request', result.request.id], result)
    void queryClient.invalidateQueries({ queryKey: ['requests'] })
    void queryClient.invalidateQueries({ queryKey: ['history', result.request.id] })
    loadedFor.current = result.request.id
    setDraft(toDraft(result.request))
    setEvaluation(result.evaluation)
    setDirty(false)
    setActionError(null)
  }

  const fail = (error: unknown) => {
    setActionError(error)
    if (error instanceof ApiError && error.evaluation) {
      setEvaluation(error.evaluation)
      setShowAllErrors(true)
    }
  }

  const save = useMutation({
    mutationFn: () => (id ? api.updateRequest(id, draft) : api.createRequest(draft)),
    onSuccess: (result) => {
      accept(result)
      if (!id) navigate(`/requests/${result.request.id}`, { replace: true })
    },
    onError: fail,
  })

  const submit = useMutation({
    mutationFn: async () => {
      setShowAllErrors(true)
      const saved = id ? (dirty ? await api.updateRequest(id, draft) : loaded.data!) : await api.createRequest(draft)
      if (!id) navigate(`/requests/${saved.request.id}`, { replace: true })
      return api.submit(saved.request.id)
    },
    onSuccess: accept,
    onError: fail,
  })

  const remove = useMutation({
    mutationFn: () => api.deleteRequest(id!),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['requests'] })
      navigate('/')
    },
    onError: fail,
  })

  if (id && loaded.isPending) return <Loading label="Loading request" />
  if (id && loaded.isError) return <ErrorNote error={loaded.error} />

  if (!editable && request) return <RequestView data={loaded.data!} simulated={meta.data?.sapSimulated ?? false} onChanged={accept} />

  const headerError = (field: string) =>
    showAllErrors ? evaluation?.messages.find((m) => m.field === field && m.severity === 'Error')?.text : undefined
  const generalMessages = evaluation?.messages.filter((m) => !m.field) ?? []
  const busy = save.isPending || submit.isPending || remove.isPending
  const total = draft.lines.reduce((sum, line) => sum + (line.quantity ?? 0) * (line.unitPrice ?? 0), 0)

  return (
    <div className="grid gap-6 xl:grid-cols-[minmax(0,1fr)_24rem]">
      <div className="space-y-4">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <h1 className="text-xl font-semibold">{request ? request.reference : 'New purchase request'}</h1>
            {request && (
              <p className="mt-1 flex items-center gap-2 text-sm text-slate-600">
                <StatusBadge status={request.status} /> {dirty && <span>Unsaved changes</span>}
              </p>
            )}
          </div>
          <div className="flex gap-2">
            {request?.status === 'Draft' && (
              <Button variant="danger" disabled={busy} onClick={() => window.confirm('Delete this draft? This cannot be undone.') && remove.mutate()}>
                Delete draft
              </Button>
            )}
            <Button disabled={busy} onClick={() => save.mutate()}>
              {save.isPending ? 'Saving…' : 'Save draft'}
            </Button>
            <Button variant="primary" disabled={busy} onClick={() => submit.mutate()}>
              {submit.isPending ? 'Submitting…' : 'Submit for review'}
            </Button>
          </div>
        </div>

        {request?.status === 'SentBack' && (
          <Note tone="warn">
            <strong>{request.reviewerName} sent this back:</strong> {request.reviewComment}
          </Note>
        )}
        <ErrorNote error={actionError} />

        <Card title="Request">
          <div className="grid grid-cols-2 gap-3 md:grid-cols-12">
            <Field label="Title" required error={headerError('title')} className="col-span-2 md:col-span-6">
              <input className={inputClass} value={draft.title} maxLength={200} onChange={(event) => change({ title: event.target.value })} />
            </Field>
            <Field label="Section" required error={headerError('sectionId')} hint="Decides who reviews this request." className="col-span-2 md:col-span-3">
              <select className={inputClass} value={draft.sectionId ?? ''} onChange={(event) => change({ sectionId: event.target.value || null })}>
                <option value="">Choose</option>
                {sections.data?.map((section) => (
                  <option key={section.id} value={section.id}>
                    {section.name}
                    {section.hasReviewer ? '' : ' (no reviewer yet)'}
                  </option>
                ))}
              </select>
            </Field>
            <Field label="Plant" required error={headerError('plant')} className="md:col-span-2">
              <MasterSelect kind="plants" value={draft.plant} onChange={(code) => change({ plant: code ?? '' })} />
            </Field>
            <Field label="Currency" className="md:col-span-1">
              <input className={inputClass} value={draft.currency} maxLength={3} onChange={(event) => change({ currency: event.target.value.toUpperCase() })} />
            </Field>
            <Field label="Why is this needed?" className="col-span-2 md:col-span-12">
              <textarea className={inputClass} rows={2} value={draft.justification} onChange={(event) => change({ justification: event.target.value })} />
            </Field>
          </div>
        </Card>

        <div className="space-y-3">
          {draft.lines.map((line) => (
            <LineEditor
              key={line.lineNo}
              line={line}
              evaluation={evaluation?.lines.find((e) => e.lineNo === line.lineNo)}
              currency={draft.currency}
              disabled={busy}
              showAllErrors={showAllErrors}
              onChange={(patch) => changeLine(line.lineNo, patch)}
              onRemove={() => removeLine(line.lineNo)}
            />
          ))}
          {showAllErrors && headerError('lines') && <Note tone="bad">{headerError('lines')}</Note>}
          <div className="flex items-center justify-between">
            <Button onClick={addLine} disabled={busy}>
              Add line
            </Button>
            <span className="text-sm text-slate-700">
              Total <strong>{money(total, draft.currency)}</strong>
            </span>
          </div>
        </div>

        <Checks evaluation={evaluation} messages={generalMessages} currency={draft.currency} />

        {request ? (
          <Attachments request={request} editable onChanged={() => void queryClient.invalidateQueries({ queryKey: ['request', request.id] })} />
        ) : (
          <Card title="Attachments">
            <p className="text-sm text-slate-500">Save the draft first, then attach the quotation.</p>
          </Card>
        )}
      </div>

      <aside className="xl:sticky xl:top-20 xl:h-[calc(100vh-7rem)]">
        <ChatPanel draft={draft} requestId={id ?? null} onApply={applyProposal} />
      </aside>
    </div>
  )
}

/** Split preview and budget check: what will happen in SAP if this is approved. */
function Checks({ evaluation, messages, currency }: { evaluation: Evaluation | null; messages: Evaluation['messages']; currency: string }) {
  if (!evaluation) return null
  return (
    <Card title="Before you submit">
      <div className="space-y-3">
        {messages.map((message, index) => (
          <Note key={index} tone={message.severity === 'Error' ? 'bad' : message.severity === 'Warning' ? 'warn' : 'info'}>
            {message.text}
          </Note>
        ))}

        {evaluation.splits.length > 0 && (
          <div>
            <h3 className="mb-1 text-xs font-medium text-slate-600">
              {evaluation.splits.length === 1 ? 'This becomes 1 SAP PR' : `This becomes ${evaluation.splits.length} SAP PRs`}
            </h3>
            <ul className="space-y-1">
              {evaluation.splits.map((split, index) => (
                <li key={split.key} className="flex flex-wrap items-center justify-between gap-2 rounded-md bg-slate-50 px-3 py-2 text-sm">
                  <span>
                    <strong>PR {index + 1}</strong>: lines {split.lineNos.join(', ')} <span className="text-slate-500">({split.label})</span>
                  </span>
                  <span>{money(split.total, currency)}</span>
                </li>
              ))}
            </ul>
          </div>
        )}

        {evaluation.budget.length > 0 && (
          <div>
            <h3 className="mb-1 text-xs font-medium text-slate-600">Budget, from SAP</h3>
            <ul className="space-y-1">
              {evaluation.budget.map((check) => (
                <li key={`${check.objectType}-${check.objectCode}`} className="flex items-start gap-2 rounded-md bg-slate-50 px-3 py-2 text-sm">
                  <Badge tone={check.isOver ? 'warn' : check.remaining === null ? 'neutral' : 'good'}>{check.isOver ? 'Over budget' : check.remaining === null ? 'Unknown' : 'Within budget'}</Badge>
                  <span>{check.text}</span>
                </li>
              ))}
            </ul>
            <p className="mt-1 text-xs text-slate-500">A budget warning does not stop you. The binding check happens in SAP.</p>
          </div>
        )}
      </div>
    </Card>
  )
}

function Attachments({ request, editable, onChanged }: { request: RequestDto; editable: boolean; onChanged: () => void }) {
  const [error, setError] = useState<unknown>(null)
  const [busy, setBusy] = useState(false)
  const input = useRef<HTMLInputElement>(null)

  const run = async (work: () => Promise<unknown>) => {
    setBusy(true)
    setError(null)
    try {
      await work()
      onChanged()
    } catch (caught) {
      setError(caught)
    } finally {
      setBusy(false)
      if (input.current) input.current.value = ''
    }
  }

  return (
    <Card
      title="Attachments"
      actions={
        editable && (
          <>
            <input
              ref={input}
              type="file"
              className="hidden"
              accept=".pdf,.png,.jpg,.jpeg,.xlsx,.xls,.docx,.doc,.csv,.txt,.msg,.eml"
              onChange={(event) => event.target.files?.[0] && void run(() => api.uploadAttachment(request.id, event.target.files![0]))}
            />
            <Button small disabled={busy} onClick={() => input.current?.click()}>
              {busy ? 'Working…' : 'Attach quotation'}
            </Button>
          </>
        )
      }
    >
      <div className="space-y-2">
        <ErrorNote error={error} />
        {request.attachments.length === 0 && <p className="text-sm text-slate-500">No attachments.</p>}
        <ul className="divide-y divide-slate-100">
          {request.attachments.map((attachment) => (
            <li key={attachment.id} className="flex items-center justify-between gap-3 py-2 text-sm">
              <button type="button" className="truncate text-left text-teal-800 underline-offset-2 hover:underline" onClick={() => void run(() => api.downloadAttachment(request.id, attachment))}>
                {attachment.fileName}
              </button>
              <span className="flex shrink-0 items-center gap-3 text-xs text-slate-500">
                {Math.max(1, Math.round(attachment.sizeBytes / 1024))} KB
                {editable && (
                  <Button variant="ghost" small disabled={busy} onClick={() => void run(() => api.removeAttachment(request.id, attachment.id))}>
                    Remove
                  </Button>
                )}
              </span>
            </li>
          ))}
        </ul>
      </div>
    </Card>
  )
}

/** Read-only view of a submitted request, with the reviewer's decision and the SAP result. */
function RequestView({ data, simulated, onChanged }: { data: RequestWithEvaluation; simulated: boolean; onChanged: (result: RequestWithEvaluation) => void }) {
  const { request, evaluation } = data
  const [comment, setComment] = useState('')
  const [error, setError] = useState<unknown>(null)
  const history = useQuery({ queryKey: ['history', request.id, request.status], queryFn: () => api.history(request.id) })

  const act = useMutation({
    mutationFn: (action: 'approve' | 'sendBack' | 'retry') =>
      action === 'approve' ? api.approve(request.id, comment) : action === 'sendBack' ? api.sendBack(request.id, comment) : api.retry(request.id),
    onSuccess: (result) => {
      setComment('')
      setError(null)
      onChanged(result)
    },
    onError: setError,
  })

  const splitOf = (splitId: string | null | undefined) => request.splits.find((s) => s.id === splitId)

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <p className="text-sm text-slate-500">
            <Link to="/" className="hover:underline">
              Requests
            </Link>{' '}
            / {request.reference}
          </p>
          <h1 className="mt-1 text-xl font-semibold">{request.title}</h1>
          <p className="mt-1 flex flex-wrap items-center gap-2 text-sm text-slate-600">
            <StatusBadge status={request.status} />
            <span>
              {request.requestorName} · {request.sectionName ?? 'No section'} · Plant {request.plant} · {money(request.total, request.currency)}
            </span>
          </p>
        </div>
      </div>

      {request.justification && <Note tone="neutral">{request.justification}</Note>}
      {request.reviewComment && (
        <Note tone={request.status === 'SentBack' ? 'warn' : 'neutral'}>
          <strong>{request.reviewerName}:</strong> {request.reviewComment}
        </Note>
      )}

      {request.splits.length > 0 && (
        <Card title="SAP purchase requisitions">
          <div className="space-y-3">
            {simulated && (
              <Note tone="warn">
                <strong>SIMULATED, not in SAP.</strong> SAP is not connected yet. These PR numbers come from the mock adapter; no purchase requisition exists in SAP.
              </Note>
            )}
            <ul className="space-y-2">
              {request.splits.map((split) => (
                <li key={split.id} className="rounded-md bg-slate-50 px-3 py-2 text-sm">
                  <div className="flex flex-wrap items-center gap-2">
                    <strong>{split.sapPrNumber ? `PR ${split.sapPrNumber}` : `PR ${split.sequence} of ${request.splits.length}`}</strong>
                    {split.isSimulated && <SimulatedTag />}
                    <Badge tone={split.status === 'Created' ? 'good' : split.status === 'Failed' ? 'bad' : 'info'}>{split.status === 'Pending' ? 'Waiting' : split.status}</Badge>
                    <span className="text-slate-500">{split.label}</span>
                  </div>
                  {split.lastError && <p className="mt-1 text-xs text-rose-700">SAP said: {split.lastError}</p>}
                </li>
              ))}
            </ul>
            {request.status === 'Created' && <p className="text-xs text-slate-500">Next: approval in SAP according to the Manual of Authority, then procurement.</p>}
            {request.permissions.canRetry && (
              <Button variant="primary" disabled={act.isPending} onClick={() => act.mutate('retry')}>
                Try SAP creation again
              </Button>
            )}
          </div>
        </Card>
      )}

      <Card title={`Lines (${request.lines.length})`}>
        <Table head={['#', 'Description', 'Qty', 'Unit price', 'Total', 'Charge to', 'Doc type', 'Acct. assignment', 'Item cat.', 'G/L', 'Purch. group', 'Material group', 'Delivery', 'SAP PR']}>
          {request.lines.map((line) => (
            <tr key={line.lineNo}>
              <td className={cell}>{line.lineNo}</td>
              <td className={cell}>
                <span className="font-medium">{line.shortText}</span>
                <span className="block text-xs text-slate-500">
                  {line.itemType} · {line.budgetType?.toUpperCase()}
                  {line.materialNumber ? ` · ${line.materialNumber}` : ''}
                </span>
              </td>
              <td className={cell}>
                {line.quantity} {line.uom}
              </td>
              <td className={cell}>{money(line.unitPrice)}</td>
              <td className={cell}>{money((line.quantity ?? 0) * (line.unitPrice ?? 0))}</td>
              <td className={cell}>{[line.costCenter, line.wbs, line.assetNumber && `Asset ${line.assetNumber}`].filter(Boolean).join(' · ') || '-'}</td>
              <td className={cell}>{sapValue(line.prDocType)}</td>
              <td className={cell}>{sapValue(line.acctAssignmentCategory)}</td>
              <td className={cell}>{sapValue(line.itemCategory)}</td>
              <td className={cell}>{sapValue(line.glAccount)}</td>
              <td className={cell}>{sapValue(line.purchasingGroup)}</td>
              <td className={cell}>{sapValue(line.materialGroup)}</td>
              <td className={cell}>{line.deliveryDate}</td>
              <td className={cell}>{splitOf(line.splitId)?.sapPrNumber ?? (splitOf(line.splitId) ? `PR ${splitOf(line.splitId)!.sequence}` : '-')}</td>
            </tr>
          ))}
        </Table>
      </Card>

      {request.status === 'Submitted' && <Checks evaluation={evaluation} messages={evaluation.messages.filter((m) => !m.field)} currency={request.currency} />}

      <Attachments request={request} editable={false} onChanged={() => undefined} />

      {request.permissions.canReview && (
        <Card title="Your decision">
          <div className="space-y-3">
            {!evaluation.canSubmit && (
              <Note tone="bad">This request no longer passes validation (rules or master data changed since it was submitted). It can only be sent back.</Note>
            )}
            <Field label="Comment" hint="Required when sending back. Optional when approving.">
              <textarea className={inputClass} rows={2} value={comment} onChange={(event) => setComment(event.target.value)} />
            </Field>
            <ErrorNote error={error} />
            <div className="flex flex-wrap items-center gap-2">
              <Button variant="primary" disabled={act.isPending || !evaluation.canSubmit} onClick={() => act.mutate('approve')}>
                Approve and create in SAP
              </Button>
              <Button disabled={act.isPending || !comment.trim()} onClick={() => act.mutate('sendBack')}>
                Send back
              </Button>
              <span className="text-xs text-slate-500">
                Approving creates {evaluation.splits.length === 1 ? '1 PR' : `${evaluation.splits.length} PRs`} in SAP{simulated ? ' (simulated)' : ''}.
              </span>
            </div>
          </div>
        </Card>
      )}
      {!request.permissions.canReview && <ErrorNote error={error} />}

      <Card title="History">
        {history.isPending ? (
          <Loading />
        ) : (
          <ol className="space-y-2">
            {history.data?.map((event) => (
              <li key={event.id} className="flex flex-wrap gap-x-3 text-sm">
                <span className="w-40 shrink-0 text-xs text-slate-500">{dateTime(event.at)}</span>
                <span className="font-medium">{HISTORY_LABELS[event.action] ?? event.action}</span>
                <span className="text-slate-500">{event.actorEmail === 'system' ? 'System' : event.actorEmail}</span>
              </li>
            ))}
          </ol>
        )}
      </Card>
    </div>
  )
}

const HISTORY_LABELS: Record<string, string> = {
  'request.created': 'Draft created',
  'request.updated': 'Draft saved',
  'request.submitted': 'Submitted for review',
  'request.sent_back': 'Sent back',
  'request.approved': 'Approved',
  'request.retry_requested': 'SAP creation retried',
  'request.created_in_sap': 'All PRs created in SAP',
  'sap.pr_created': 'PR created in SAP',
  'sap.pr_failed': 'SAP rejected a PR',
  'sap.attachment_link_failed': 'Attachment could not be linked in SAP',
  'attachment.added': 'Attachment added',
  'attachment.removed': 'Attachment removed',
}
