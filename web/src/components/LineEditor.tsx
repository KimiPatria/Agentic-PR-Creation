import type { LineEvaluation, LineInput } from '../api'
import { Badge, Button, Field, LockIcon, inputClass, money, sapValue } from '../ui'
import { MasterSearch, MasterSelect } from './MasterData'

const SAP_FIELDS: { field: 'prDocType' | 'acctAssignmentCategory' | 'itemCategory' | 'glAccount' | 'purchasingGroup'; label: string }[] = [
  { field: 'prDocType', label: 'PR document type' },
  { field: 'acctAssignmentCategory', label: 'Account assignment' },
  { field: 'itemCategory', label: 'Item category' },
  { field: 'glAccount', label: 'G/L account' },
  { field: 'purchasingGroup', label: 'Purchasing group' },
]

const CHOICES: Record<string, { value: string; label: string }[]> = {
  itemType: [
    { value: 'Material', label: 'Material (goods)' },
    { value: 'Service', label: 'Service (work)' },
  ],
  budgetType: [
    { value: 'Opex', label: 'OPEX (running cost)' },
    { value: 'Capex', label: 'CAPEX (asset)' },
  ],
  costObjectType: [
    { value: 'CostCenter', label: 'Cost center' },
    { value: 'Wbs', label: 'Project (WBS element)' },
  ],
}

/**
 * One line of the guided form. The requestor fills the top rows; the bottom row shows the SAP fields
 * the rules engine derived, locked. Choices that cannot lead to a valid combination are disabled.
 */
export function LineEditor({
  line,
  evaluation,
  currency,
  disabled,
  showAllErrors,
  onChange,
  onRemove,
}: {
  line: LineInput
  evaluation: LineEvaluation | undefined
  currency: string
  disabled: boolean
  showAllErrors: boolean
  onChange: (patch: Partial<LineInput>) => void
  onRemove: () => void
}) {
  const resolved = evaluation?.resolved
  const locked = (field: string) => evaluation?.lockReasons[field]
  const required = (field: string) => evaluation?.requiredFields.includes(field) ?? false

  // A locked field shows what the rules engine resolved, not what was typed.
  const shown = <K extends keyof LineInput>(field: K): LineInput[K] | undefined =>
    locked(field) && resolved ? (resolved[field] as LineInput[K]) : line[field]

  const error = (field: string): string | undefined => {
    const hasValue = line[field as keyof LineInput] !== null && line[field as keyof LineInput] !== undefined && line[field as keyof LineInput] !== ''
    return evaluation?.messages.find(
      (m) => m.field === field && m.severity === 'Error' && (showAllErrors || hasValue || !m.text.endsWith('is required.')),
    )?.text
  }

  const choice = (field: 'itemType' | 'budgetType' | 'costObjectType', label: string) => {
    const allowed = evaluation?.allowedOptions[field]
    return (
      <Field label={label} required lockReason={locked(field)} error={error(field)}>
        <select
          className={inputClass}
          value={(shown(field) as string | null | undefined) ?? ''}
          disabled={disabled || !!locked(field)}
          onChange={(event) => onChange({ [field]: event.target.value || null })}
        >
          <option value="">Choose</option>
          {CHOICES[field].map((option) => (
            <option key={option.value} value={option.value} disabled={!!allowed && !allowed.includes(option.value)}>
              {option.label}
              {allowed && !allowed.includes(option.value) ? ' (not allowed here)' : ''}
            </option>
          ))}
        </select>
      </Field>
    )
  }

  const itemType = shown('itemType')
  const costObject = shown('costObjectType')
  const showCostCenter = required('costCenter') || (costObject === 'CostCenter' && !locked('costCenter')) || !!line.costCenter
  const showWbs = required('wbs') || (costObject === 'Wbs' && !locked('wbs')) || !!line.wbs
  const showAsset = required('assetNumber') || !!line.assetNumber
  const otherMessages = evaluation?.messages.filter((m) => m.severity === 'Warning' || (m.severity === 'Error' && !m.field)) ?? []
  const total = (line.quantity ?? 0) * (line.unitPrice ?? 0)

  return (
    <div className="rounded-lg border border-slate-200 bg-white" data-testid={`line-${line.lineNo}`}>
      <div className="flex items-center justify-between border-b border-slate-100 px-4 py-2">
        <div className="flex items-center gap-2">
          <span className="text-sm font-semibold">Line {line.lineNo}</span>
          {evaluation && !evaluation.isValid && showAllErrors && <Badge tone="bad">Needs attention</Badge>}
          {evaluation?.isValid && <Badge tone="good">Complete</Badge>}
        </div>
        <div className="flex items-center gap-3">
          <span className="text-sm text-slate-600">{money(total, currency)}</span>
          {!disabled && (
            <Button variant="ghost" small onClick={onRemove} aria-label={`Remove line ${line.lineNo}`}>
              Remove
            </Button>
          )}
        </div>
      </div>

      <div className="grid grid-cols-2 gap-3 p-4 md:grid-cols-12">
        <Field label="What are you buying?" required error={error('shortText')} className="col-span-2 md:col-span-5" hint="Up to 40 characters reach SAP.">
          <input className={inputClass} value={line.shortText ?? ''} disabled={disabled} maxLength={80} onChange={(event) => onChange({ shortText: event.target.value })} />
        </Field>

        <Field
          label="SAP material number"
          required={required('materialNumber')}
          error={error('materialNumber')}
          hint="Only for items in the material master."
          className="col-span-2 md:col-span-3"
        >
          <MasterSearch
            kind="materials"
            value={line.materialNumber}
            disabled={disabled || itemType === 'Service'}
            placeholder={itemType === 'Service' ? 'Not for services' : 'Search, or leave empty'}
            onChange={(code, option) => onChange({ materialNumber: code, ...(option && !line.shortText ? { shortText: option.name.slice(0, 40) } : {}) })}
          />
        </Field>

        <Field label="Quantity" required error={error('quantity')} className="md:col-span-2">
          <input
            className={inputClass}
            type="number"
            min={0}
            step="any"
            value={line.quantity ?? ''}
            disabled={disabled}
            onChange={(event) => onChange({ quantity: event.target.value === '' ? null : Number(event.target.value) })}
          />
        </Field>

        <Field label="Unit" required lockReason={locked('uom')} error={error('uom')} className="md:col-span-2">
          <MasterSelect kind="uoms" value={shown('uom')} disabled={disabled || !!locked('uom')} onChange={(code) => onChange({ uom: code })} />
        </Field>

        <div className="md:col-span-3">{choice('itemType', 'Item type')}</div>
        <div className="md:col-span-3">{choice('budgetType', 'Budget type')}</div>
        <div className="md:col-span-3">{choice('costObjectType', 'Charge to')}</div>

        <Field label="Material group" required lockReason={locked('materialGroup')} error={error('materialGroup')} className="col-span-2 md:col-span-3">
          <MasterSelect
            kind="material-groups"
            value={shown('materialGroup')}
            disabled={disabled || !!locked('materialGroup')}
            filter={(option) => !itemType || !option.meta?.itemType || option.meta.itemType === itemType}
            onChange={(code) => onChange({ materialGroup: code })}
          />
        </Field>

        <Field label={`Unit price${currency ? ` (${currency})` : ''}`} required error={error('unitPrice')} className="md:col-span-3">
          <input
            className={inputClass}
            type="number"
            min={0}
            step="any"
            value={line.unitPrice ?? ''}
            disabled={disabled}
            onChange={(event) => onChange({ unitPrice: event.target.value === '' ? null : Number(event.target.value) })}
          />
        </Field>

        <Field label="Delivery date" required error={error('deliveryDate')} className="md:col-span-3">
          <input className={inputClass} type="date" value={line.deliveryDate ?? ''} disabled={disabled} onChange={(event) => onChange({ deliveryDate: event.target.value || null })} />
        </Field>

        {showCostCenter && (
          <Field label="Cost center" required={required('costCenter')} lockReason={locked('costCenter')} error={error('costCenter')} className="col-span-2 md:col-span-3">
            <MasterSearch kind="cost-centers" value={shown('costCenter')} disabled={disabled || !!locked('costCenter')} onChange={(code) => onChange({ costCenter: code })} />
          </Field>
        )}
        {showWbs && (
          <Field label="WBS element" required={required('wbs')} lockReason={locked('wbs')} error={error('wbs')} className="col-span-2 md:col-span-3">
            <MasterSearch kind="wbs" value={shown('wbs')} disabled={disabled || !!locked('wbs')} onChange={(code) => onChange({ wbs: code })} />
          </Field>
        )}
        {showAsset && (
          <Field label="Asset number" required={required('assetNumber')} lockReason={locked('assetNumber')} error={error('assetNumber')} className="col-span-2 md:col-span-3">
            <MasterSearch kind="assets" value={shown('assetNumber')} disabled={disabled || !!locked('assetNumber')} onChange={(code) => onChange({ assetNumber: code })} />
          </Field>
        )}
      </div>

      <div className="border-t border-slate-100 bg-slate-50 px-4 py-3">
        <div className="mb-2 flex flex-wrap items-center gap-2 text-xs text-slate-500">
          <LockIcon />
          <span>SAP fields, set by the rules engine</span>
          {evaluation?.matchedRuleCode && <Badge tone="neutral">Rule {evaluation.matchedRuleCode}</Badge>}
          {evaluation?.matchedRuleIsPlaceholder && (
            <Badge tone="warn" title="The real rule table has not been loaded yet. This row is a stand-in.">
              Placeholder rule
            </Badge>
          )}
          {resolved?.isStock && <Badge tone="info">Stock item</Badge>}
        </div>
        <dl className="grid grid-cols-2 gap-x-4 gap-y-2 sm:grid-cols-5">
          {SAP_FIELDS.map(({ field, label }) => (
            <div key={field} title={locked(field) ?? 'Set once the lines above are complete.'}>
              <dt className="text-xs text-slate-500">{label}</dt>
              <dd className="text-sm font-medium text-slate-800">{sapValue(resolved?.[field])}</dd>
              {error(field) && <dd className="text-xs text-rose-700">{error(field)}</dd>}
            </div>
          ))}
        </dl>
      </div>

      {otherMessages.length > 0 && (
        <ul className="space-y-1 border-t border-slate-100 px-4 py-3">
          {otherMessages.map((message, index) => (
            <li key={index} className={`text-xs ${message.severity === 'Error' ? 'text-rose-700' : 'text-amber-800'}`}>
              {message.severity === 'Error' ? 'Error' : 'Warning'}: {message.text}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
