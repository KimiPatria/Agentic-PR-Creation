import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import type { AdminSection, MaterialGroup, Rule } from '../api'
import { api } from '../api'
import { useMasterList } from '../components/MasterData'
import { Badge, Button, Card, Empty, ErrorNote, Field, Loading, Modal, Note, SimulatedTag, Table, cell, dateTime, inputClass, sapValue } from '../ui'

const TABS = ['Rules', 'Sections', 'Material groups', 'Jobs', 'Audit', 'SAP'] as const
type Tab = (typeof TABS)[number]

const FACT_LABELS: Record<string, string> = { budgetType: 'Budget', itemType: 'Item', costObjectType: 'Charge to', isStock: 'Stock' }
const FIELD_LABELS: Record<string, string> = { materialNumber: 'Material number', costCenter: 'Cost center', wbs: 'WBS element', assetNumber: 'Asset number' }

/** What the PR admin maintains, and the monitor of background work. */
export function AdminPage() {
  const [tab, setTab] = useState<Tab>('Rules')
  return (
    <div className="space-y-4">
      <h1 className="text-xl font-semibold">Administration</h1>
      <div className="flex flex-wrap gap-1 border-b border-slate-200" role="tablist">
        {TABS.map((name) => (
          <button
            key={name}
            type="button"
            role="tab"
            aria-selected={tab === name}
            onClick={() => setTab(name)}
            className={`-mb-px border-b-2 px-3 py-2 text-sm font-medium ${tab === name ? 'border-teal-700 text-teal-800' : 'border-transparent text-slate-500 hover:text-slate-800'}`}
          >
            {name}
          </button>
        ))}
      </div>
      {tab === 'Rules' && <RulesTab />}
      {tab === 'Sections' && <SectionsTab />}
      {tab === 'Material groups' && <MaterialGroupsTab />}
      {tab === 'Jobs' && <JobsTab />}
      {tab === 'Audit' && <AuditTab />}
      {tab === 'SAP' && <SapTab />}
    </div>
  )
}

// ---- Rules -----------------------------------------------------------------------------------

const NEW_RULE: Rule = {
  id: '', code: '', description: '', conditions: {}, priority: 0, allowed: true, message: null, prDocType: 'NB', acctAssignmentCategory: '',
  itemCategory: '', glMode: 'None', glFixedAccount: null, requiredFields: [], isPlaceholder: false, isActive: true, validFrom: new Date().toISOString().slice(0, 10), validTo: null,
}

function RulesTab() {
  const queryClient = useQueryClient()
  const rules = useQuery({ queryKey: ['admin', 'rules'], queryFn: api.admin.rules })
  const [editing, setEditing] = useState<Rule | null>(null)

  if (rules.isPending) return <Loading />
  if (rules.isError) return <ErrorNote error={rules.error} />

  const placeholders = rules.data.rules.filter((r) => r.isPlaceholder).length

  return (
    <div className="space-y-3">
      {placeholders > 0 && (
        <Note tone="warn">
          <strong>
            {placeholders} of {rules.data.rules.length} rules are placeholders.
          </strong>{' '}
          They stand in for the real rule table, which has not been written down yet. Editing a row makes it yours and removes the placeholder mark.
        </Note>
      )}
      <Card
        title="Rule table"
        actions={
          <Button small variant="primary" onClick={() => setEditing(NEW_RULE)}>
            Add rule
          </Button>
        }
      >
        <p className="mb-3 text-xs text-slate-500">
          A rule applies when all its conditions match the line. If several apply, the one with the most conditions wins, then the highest priority. A change takes effect on
          the next evaluation; requests already submitted must be sent back and resubmitted to pick it up.
        </p>
        <Table head={['Code', 'When', 'Result', 'Doc type', 'Acct. assign.', 'Item cat.', 'G/L', 'Requires', '']}>
          {rules.data.rules.map((rule) => (
            <tr key={rule.id} className={rule.isActive ? '' : 'opacity-50'}>
              <td className={cell}>
                <span className="font-medium">{rule.code}</span>
                {rule.isPlaceholder && (
                  <span className="mt-1 block">
                    <Badge tone="warn">Placeholder</Badge>
                  </span>
                )}
              </td>
              <td className={cell}>
                <span className="flex max-w-64 flex-wrap gap-1">
                  {Object.entries(rule.conditions).map(([fact, value]) => (
                    <Badge key={fact}>
                      {FACT_LABELS[fact] ?? fact}: {value}
                    </Badge>
                  ))}
                  {Object.keys(rule.conditions).length === 0 && <span className="text-xs text-slate-500">Always</span>}
                </span>
              </td>
              <td className={cell}>
                {rule.allowed ? <Badge tone="good">Allowed</Badge> : <Badge tone="bad">Forbidden</Badge>}
                {!rule.allowed && <span className="mt-1 block max-w-56 text-xs text-slate-500">{rule.message}</span>}
              </td>
              <td className={cell}>{rule.allowed ? sapValue(rule.prDocType) : '-'}</td>
              <td className={cell}>{rule.allowed ? sapValue(rule.acctAssignmentCategory) : '-'}</td>
              <td className={cell}>{rule.allowed ? sapValue(rule.itemCategory) : '-'}</td>
              <td className={cell}>{!rule.allowed ? '-' : rule.glMode === 'Fixed' ? rule.glFixedAccount : rule.glMode === 'FromMaterialGroup' ? 'From material group' : 'None'}</td>
              <td className={cell}>{rule.requiredFields.map((f) => FIELD_LABELS[f] ?? f).join(', ') || '-'}</td>
              <td className={cell}>
                <Button small onClick={() => setEditing(rule)}>
                  Edit
                </Button>
              </td>
            </tr>
          ))}
        </Table>
      </Card>

      {editing && (
        <RuleEditor
          rule={editing}
          factDomains={rules.data.factDomains}
          requirableFields={rules.data.requirableFields}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null)
            void queryClient.invalidateQueries({ queryKey: ['admin', 'rules'] })
          }}
        />
      )}
    </div>
  )
}

function RuleEditor({
  rule,
  factDomains,
  requirableFields,
  onClose,
  onSaved,
}: {
  rule: Rule
  factDomains: Record<string, string[]>
  requirableFields: string[]
  onClose: () => void
  onSaved: () => void
}) {
  const [value, setValue] = useState<Rule>(rule)
  const set = (patch: Partial<Rule>) => setValue((current) => ({ ...current, ...patch }))
  const save = useMutation({ mutationFn: () => api.admin.saveRule(value), onSuccess: onSaved })
  const remove = useMutation({ mutationFn: () => api.admin.deleteRule(value.id), onSuccess: onSaved })

  return (
    <Modal title={rule.id ? `Rule ${rule.code}` : 'New rule'} onClose={onClose} wide>
      <div className="space-y-4">
        <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
          <Field label="Code" required>
            <input className={inputClass} value={value.code} onChange={(e) => set({ code: e.target.value })} />
          </Field>
          <Field label="Description" required className="sm:col-span-3">
            <input className={inputClass} value={value.description} onChange={(e) => set({ description: e.target.value })} />
          </Field>
        </div>

        <fieldset className="rounded-md border border-slate-200 p-3">
          <legend className="px-1 text-xs font-medium text-slate-600">Applies when</legend>
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
            {Object.entries(factDomains).map(([fact, domain]) => (
              <Field key={fact} label={FACT_LABELS[fact] ?? fact}>
                <select
                  className={inputClass}
                  value={value.conditions[fact] ?? ''}
                  onChange={(e) => {
                    const conditions = { ...value.conditions }
                    if (e.target.value) conditions[fact] = e.target.value
                    else delete conditions[fact]
                    set({ conditions })
                  }}
                >
                  <option value="">Any</option>
                  {domain.map((option) => (
                    <option key={option} value={option}>
                      {option}
                    </option>
                  ))}
                </select>
              </Field>
            ))}
          </div>
        </fieldset>

        <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
          <Field label="Result">
            <select className={inputClass} value={value.allowed ? 'yes' : 'no'} onChange={(e) => set({ allowed: e.target.value === 'yes' })}>
              <option value="yes">Allowed</option>
              <option value="no">Forbidden</option>
            </select>
          </Field>
          <Field label="Priority" hint="Higher wins a tie.">
            <input className={inputClass} type="number" value={value.priority} onChange={(e) => set({ priority: Number(e.target.value) })} />
          </Field>
          <Field label="Valid from" required>
            <input className={inputClass} type="date" value={value.validFrom} onChange={(e) => set({ validFrom: e.target.value })} />
          </Field>
          <Field label="Valid to">
            <input className={inputClass} type="date" value={value.validTo ?? ''} onChange={(e) => set({ validTo: e.target.value || null })} />
          </Field>
        </div>

        {value.allowed ? (
          <fieldset className="rounded-md border border-slate-200 p-3">
            <legend className="px-1 text-xs font-medium text-slate-600">Sets these SAP fields (locked on the form)</legend>
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
              <Field label="PR document type" required>
                <input className={inputClass} value={value.prDocType ?? ''} onChange={(e) => set({ prDocType: e.target.value })} />
              </Field>
              <Field label="Account assignment" hint="Empty means SAP blank.">
                <input className={inputClass} value={value.acctAssignmentCategory ?? ''} onChange={(e) => set({ acctAssignmentCategory: e.target.value })} />
              </Field>
              <Field label="Item category" hint="Empty means SAP blank.">
                <input className={inputClass} value={value.itemCategory ?? ''} onChange={(e) => set({ itemCategory: e.target.value })} />
              </Field>
              <Field label="G/L account">
                <select className={inputClass} value={value.glMode} onChange={(e) => set({ glMode: e.target.value as Rule['glMode'] })}>
                  <option value="None">None</option>
                  <option value="FromMaterialGroup">From material group</option>
                  <option value="Fixed">Fixed account</option>
                </select>
              </Field>
              {value.glMode === 'Fixed' && (
                <Field label="Fixed G/L account" required>
                  <input className={inputClass} value={value.glFixedAccount ?? ''} onChange={(e) => set({ glFixedAccount: e.target.value })} />
                </Field>
              )}
            </div>
            <div className="mt-3">
              <span className="mb-1 block text-xs font-medium text-slate-600">The requestor must fill in</span>
              <div className="flex flex-wrap gap-3">
                {requirableFields.map((field) => (
                  <label key={field} className="flex items-center gap-1.5 text-sm">
                    <input
                      type="checkbox"
                      checked={value.requiredFields.includes(field)}
                      onChange={(e) => set({ requiredFields: e.target.checked ? [...value.requiredFields, field] : value.requiredFields.filter((f) => f !== field) })}
                    />
                    {FIELD_LABELS[field] ?? field}
                  </label>
                ))}
              </div>
            </div>
          </fieldset>
        ) : (
          <Field label="Message shown to the requestor" required hint="Say why the combination is not allowed and what to do instead.">
            <textarea className={inputClass} rows={2} value={value.message ?? ''} onChange={(e) => set({ message: e.target.value })} />
          </Field>
        )}

        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={value.isActive} onChange={(e) => set({ isActive: e.target.checked })} /> Active
        </label>

        <ErrorNote error={save.error ?? remove.error} />
        <div className="flex justify-between">
          <div>
            {rule.id && (
              <Button variant="danger" disabled={remove.isPending} onClick={() => window.confirm(`Delete rule ${rule.code}? Lines that matched it will have no rule.`) && remove.mutate()}>
                Delete
              </Button>
            )}
          </div>
          <div className="flex gap-2">
            <Button onClick={onClose}>Cancel</Button>
            <Button variant="primary" disabled={save.isPending} onClick={() => save.mutate()}>
              {save.isPending ? 'Saving…' : 'Save rule'}
            </Button>
          </div>
        </div>
      </div>
    </Modal>
  )
}

// ---- Sections --------------------------------------------------------------------------------

function SectionsTab() {
  const queryClient = useQueryClient()
  const sections = useQuery({ queryKey: ['admin', 'sections'], queryFn: api.admin.sections })
  const [editing, setEditing] = useState<AdminSection | null>(null)
  const done = () => {
    setEditing(null)
    void queryClient.invalidateQueries({ queryKey: ['admin', 'sections'] })
    void queryClient.invalidateQueries({ queryKey: ['sections'] })
  }

  if (sections.isPending) return <Loading />
  if (sections.isError) return <ErrorNote error={sections.error} />

  return (
    <Card
      title="Sections and reviewers"
      actions={
        <Button small variant="primary" onClick={() => setEditing({ id: '', code: '', name: '', reviewers: [] })}>
          Add section
        </Button>
      }
    >
      <p className="mb-3 text-xs text-slate-500">
        A request goes to the reviewers of its section. A reviewer also needs the Reviewer group in Cognito, and can never review their own request.
      </p>
      <Table head={['Code', 'Name', 'Reviewers', '']}>
        {sections.data.map((section) => (
          <tr key={section.id}>
            <td className={`${cell} font-medium`}>{section.code}</td>
            <td className={cell}>{section.name}</td>
            <td className={cell}>
              {section.reviewers.length === 0 ? (
                <Badge tone="bad">No reviewer: requests cannot be submitted</Badge>
              ) : (
                section.reviewers.map((r) => (
                  <span key={r.email} className="block">
                    {r.name} <span className="text-xs text-slate-500">{r.email}</span>
                  </span>
                ))
              )}
            </td>
            <td className={cell}>
              <Button small onClick={() => setEditing(section)}>
                Edit
              </Button>
            </td>
          </tr>
        ))}
      </Table>
      {editing && <SectionEditor section={editing} onClose={() => setEditing(null)} onSaved={done} />}
    </Card>
  )
}

function SectionEditor({ section, onClose, onSaved }: { section: AdminSection; onClose: () => void; onSaved: () => void }) {
  const [value, setValue] = useState<AdminSection>({ ...section, reviewers: section.reviewers.map((r) => ({ email: r.email, name: r.name })) })
  const save = useMutation({ mutationFn: () => api.admin.saveSection(value), onSuccess: onSaved })
  const remove = useMutation({ mutationFn: () => api.admin.deleteSection(value.id), onSuccess: onSaved })
  const setReviewer = (index: number, patch: Partial<{ email: string; name: string }>) =>
    setValue((v) => ({ ...v, reviewers: v.reviewers.map((r, i) => (i === index ? { ...r, ...patch } : r)) }))

  return (
    <Modal title={section.id ? `Section ${section.code}` : 'New section'} onClose={onClose}>
      <div className="space-y-3">
        <div className="grid grid-cols-3 gap-3">
          <Field label="Code" required>
            <input className={inputClass} value={value.code} onChange={(e) => setValue({ ...value, code: e.target.value })} />
          </Field>
          <Field label="Name" required className="col-span-2">
            <input className={inputClass} value={value.name} onChange={(e) => setValue({ ...value, name: e.target.value })} />
          </Field>
        </div>
        <div>
          <span className="mb-1 block text-xs font-medium text-slate-600">Reviewers</span>
          <div className="space-y-2">
            {value.reviewers.map((reviewer, index) => (
              <div key={index} className="flex gap-2">
                <input className={inputClass} placeholder="Email" aria-label="Reviewer email" value={reviewer.email} onChange={(e) => setReviewer(index, { email: e.target.value })} />
                <input className={inputClass} placeholder="Name" aria-label="Reviewer name" value={reviewer.name} onChange={(e) => setReviewer(index, { name: e.target.value })} />
                <Button variant="ghost" small onClick={() => setValue({ ...value, reviewers: value.reviewers.filter((_, i) => i !== index) })}>
                  Remove
                </Button>
              </div>
            ))}
            <Button small onClick={() => setValue({ ...value, reviewers: [...value.reviewers, { email: '', name: '' }] })}>
              Add reviewer
            </Button>
          </div>
        </div>
        <ErrorNote error={save.error ?? remove.error} />
        <div className="flex justify-between">
          <div>
            {section.id && (
              <Button variant="danger" disabled={remove.isPending} onClick={() => window.confirm(`Delete section ${section.name}?`) && remove.mutate()}>
                Delete
              </Button>
            )}
          </div>
          <div className="flex gap-2">
            <Button onClick={onClose}>Cancel</Button>
            <Button variant="primary" disabled={save.isPending} onClick={() => save.mutate()}>
              Save section
            </Button>
          </div>
        </div>
      </div>
    </Modal>
  )
}

// ---- Material groups -------------------------------------------------------------------------

function MaterialGroupsTab() {
  const queryClient = useQueryClient()
  const groups = useQuery({ queryKey: ['admin', 'material-groups'], queryFn: api.admin.materialGroups })
  const glAccounts = useMasterList('gl-accounts')
  const purchasingGroups = useMasterList('purchasing-groups')
  const save = useMutation({
    mutationFn: (group: MaterialGroup) => api.admin.saveMaterialGroup(group),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['admin', 'material-groups'] })
      void queryClient.invalidateQueries({ queryKey: ['master-data', 'material-groups'] })
    },
  })

  if (groups.isPending) return <Loading />
  if (groups.isError) return <ErrorNote error={groups.error} />

  const change = (group: MaterialGroup, patch: Partial<MaterialGroup>) => save.mutate({ ...group, ...patch })

  return (
    <Card title="Material group mapping">
      <p className="mb-3 text-xs text-slate-500">
        SAP supplies the material groups. Here you say what each is for, and which G/L account and purchasing group its lines get. A group without a purchasing group
        cannot be used on a request. Changes save as you make them.
      </p>
      <ErrorNote error={save.error} />
      <Table head={['Group', 'Name in SAP', 'Item type', 'Default G/L account', 'Purchasing group']}>
        {groups.data.map((group) => (
          <tr key={group.code}>
            <td className={`${cell} font-medium`}>{group.code}</td>
            <td className={cell}>{group.name}</td>
            <td className={cell}>
              <select className={inputClass} value={group.itemType ?? ''} aria-label={`Item type of ${group.code}`} onChange={(e) => change(group, { itemType: (e.target.value || null) as MaterialGroup['itemType'] })}>
                <option value="">Either</option>
                <option value="Material">Material</option>
                <option value="Service">Service</option>
              </select>
            </td>
            <td className={cell}>
              <select className={inputClass} value={group.defaultGlAccount ?? ''} aria-label={`G/L account of ${group.code}`} onChange={(e) => change(group, { defaultGlAccount: e.target.value || null })}>
                <option value="">Not mapped</option>
                {glAccounts.data?.map((o) => (
                  <option key={o.code} value={o.code}>
                    {o.code} · {o.name}
                  </option>
                ))}
              </select>
            </td>
            <td className={cell}>
              <select
                className={inputClass}
                value={group.defaultPurchasingGroup ?? ''}
                aria-label={`Purchasing group of ${group.code}`}
                onChange={(e) => change(group, { defaultPurchasingGroup: e.target.value || null })}
              >
                <option value="">Not mapped</option>
                {purchasingGroups.data?.map((o) => (
                  <option key={o.code} value={o.code}>
                    {o.code} · {o.name}
                  </option>
                ))}
              </select>
            </td>
          </tr>
        ))}
      </Table>
    </Card>
  )
}

// ---- Jobs ------------------------------------------------------------------------------------

function JobsTab() {
  const queryClient = useQueryClient()
  const jobs = useQuery({ queryKey: ['admin', 'jobs'], queryFn: api.admin.jobs, refetchInterval: 5000 })
  const retry = useMutation({ mutationFn: api.admin.retryJob, onSuccess: () => queryClient.invalidateQueries({ queryKey: ['admin', 'jobs'] }) })

  if (jobs.isPending) return <Loading />
  if (jobs.isError) return <ErrorNote error={jobs.error} />

  return (
    <Card title="Background jobs (latest 200)">
      <p className="mb-3 text-xs text-slate-500">SAP PR creation and email run here after a request is approved. A failed job has used up its automatic retries.</p>
      <ErrorNote error={retry.error} />
      {jobs.data.length === 0 ? (
        <Empty>No jobs yet.</Empty>
      ) : (
        <Table head={['Created', 'Job', 'Request', 'Status', 'Attempts', 'Last error', '']}>
          {jobs.data.map((job) => (
            <tr key={job.id}>
              <td className={cell}>{dateTime(job.createdAt)}</td>
              <td className={cell}>
                <span className="block max-w-80 truncate" title={job.summary}>
                  {job.summary}
                </span>
              </td>
              <td className={cell}>
                {job.requestId ? (
                  <Link className="text-teal-800 hover:underline" to={`/requests/${job.requestId}`}>
                    {job.requestReference}
                  </Link>
                ) : (
                  '-'
                )}
              </td>
              <td className={cell}>
                <Badge tone={job.status === 'Processed' ? 'good' : job.status === 'Failed' ? 'bad' : 'info'}>{job.status === 'Processed' ? 'Done' : job.status}</Badge>
              </td>
              <td className={cell}>{job.attempts}</td>
              <td className={cell}>
                <span className="block max-w-96 text-xs text-rose-700">{job.lastError}</span>
              </td>
              <td className={cell}>
                {job.status === 'Failed' && (
                  <Button small disabled={retry.isPending} onClick={() => retry.mutate(job.id)}>
                    Retry
                  </Button>
                )}
              </td>
            </tr>
          ))}
        </Table>
      )}
    </Card>
  )
}

// ---- Audit -----------------------------------------------------------------------------------

function AuditTab() {
  const audit = useQuery({ queryKey: ['admin', 'audit'], queryFn: api.admin.audit })
  const [open, setOpen] = useState<number | null>(null)

  if (audit.isPending) return <Loading />
  if (audit.isError) return <ErrorNote error={audit.error} />

  return (
    <Card title="Audit trail (latest 200)">
      <Table head={['When', 'Who', 'Action', 'On', '']}>
        {audit.data.map((event) => (
          <tr key={event.id}>
            <td className={cell}>{dateTime(event.at)}</td>
            <td className={cell}>{event.actorEmail}</td>
            <td className={`${cell} font-medium`}>{event.action}</td>
            <td className={cell}>
              {event.entityType === 'request' ? (
                <Link className="text-teal-800 hover:underline" to={`/requests/${event.entityId}`}>
                  request
                </Link>
              ) : (
                `${event.entityType} ${event.entityId.slice(0, 8)}`
              )}
            </td>
            <td className={cell}>
              {event.data && (
                <>
                  <Button variant="ghost" small onClick={() => setOpen(open === event.id ? null : event.id)}>
                    {open === event.id ? 'Hide' : 'Details'}
                  </Button>
                  {open === event.id && <pre className="mt-1 max-w-xl overflow-x-auto rounded bg-slate-50 p-2 text-xs">{pretty(event.data)}</pre>}
                </>
              )}
            </td>
          </tr>
        ))}
      </Table>
    </Card>
  )
}

function pretty(json: string): string {
  try {
    return JSON.stringify(JSON.parse(json), null, 2)
  } catch {
    return json
  }
}

// ---- SAP -------------------------------------------------------------------------------------

function SapTab() {
  const queryClient = useQueryClient()
  const sap = useQuery({ queryKey: ['admin', 'sap'], queryFn: api.admin.sap })
  const refresh = useMutation({
    mutationFn: api.admin.refreshMasterData,
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['admin', 'sap'] })
      void queryClient.invalidateQueries({ queryKey: ['master-data'] })
    },
  })
  const [open, setOpen] = useState<string | null>(null)

  if (sap.isPending) return <Loading />
  if (sap.isError) return <ErrorNote error={sap.error} />

  return (
    <div className="space-y-3">
      <Card title="SAP connection">
        <div className="space-y-3 text-sm">
          <p className="flex items-center gap-2">
            Adapter: <strong>{sap.data.mode}</strong> {sap.data.simulated && <SimulatedTag />}
          </p>
          {sap.data.simulated ? (
            <Note tone="warn">
              SAP is not connected. Master data and budgets come from a fictional data set, and PR numbers are generated here. To connect the real system, implement the
              S4Hana adapter and set <code>Sap:Mode</code> to <code>S4Hana</code> (see docs/sap-hookup.md).
            </Note>
          ) : (
            <Note tone="good">Connected to SAP through the S4Hana adapter.</Note>
          )}
          <div className="flex items-center gap-3">
            <Button disabled={refresh.isPending} onClick={() => refresh.mutate()}>
              Reload master data from SAP
            </Button>
            <span className="text-xs text-slate-500">
              Master data is cached for up to 15 minutes, budgets for 1 minute. Last forced reload: {dateTime(sap.data.cacheInvalidatedAt)}.
            </span>
          </div>
          <ErrorNote error={refresh.error} />
        </div>
      </Card>

      {sap.data.simulatedPrs && (
        <Card title={`Simulated PRs (${sap.data.simulatedPrs.length})`}>
          <p className="mb-3 text-xs text-slate-500">Each entry is the exact payload the PR create adapter would have sent to SAP.</p>
          {sap.data.simulatedPrs.length === 0 ? (
            <Empty>Nothing has been created yet.</Empty>
          ) : (
            <ul className="divide-y divide-slate-100">
              {sap.data.simulatedPrs.map((pr) => (
                <li key={pr.prNumber} className="py-2 text-sm">
                  <div className="flex flex-wrap items-center gap-2">
                    <strong>PR {pr.prNumber}</strong> <SimulatedTag />
                    <span className="text-slate-500">
                      {String(pr.request.requestReference)} · {(pr.request.items as unknown[]).length} item(s)
                      {pr.attachments.length > 0 && ` · ${pr.attachments.join(', ')}`}
                    </span>
                    <Button variant="ghost" small onClick={() => setOpen(open === pr.prNumber ? null : pr.prNumber)}>
                      {open === pr.prNumber ? 'Hide payload' : 'Show payload'}
                    </Button>
                  </div>
                  {open === pr.prNumber && <pre className="mt-2 overflow-x-auto rounded bg-slate-50 p-2 text-xs">{JSON.stringify(pr.request, null, 2)}</pre>}
                </li>
              ))}
            </ul>
          )}
        </Card>
      )}
    </div>
  )
}
