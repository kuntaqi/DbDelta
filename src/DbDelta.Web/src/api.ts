export type EnvironmentClass = 0 | 1 | 2 | 3

export const environmentName = ['Unknown', 'Dev', 'UAT', 'Prod'] as const

export interface ConnectionRequest {
  server: string
  database: string
}

export interface ProbeResponse {
  server: string
  database: string
  productVersion: string
  edition: string
  collation: string
  environment: EnvironmentClass
  readOnly: boolean
  tableCount: number
  viewCount: number
  routineCount: number
}

export interface ObjectSummary {
  id: string
  type: string
  schema: string
  name: string
  qualifiedName: string
  kind: 'Same' | 'SourceOnly' | 'TargetOnly' | 'Different'
  summary: string
  changedChildren: number
}

export interface TypeCount {
  type: string
  changed: number
  total: number
}

export interface CompareResponse {
  id: string
  sourceDatabase: string
  targetDatabase: string
  sourceEnvironment: EnvironmentClass
  targetEnvironment: EnvironmentClass
  targetReadOnly: boolean
  durationMs: number
  targetIsEmpty: boolean
  collationWarning: string | null
  counts: TypeCount[]
  objects: ObjectSummary[]
}

export interface PropertyDto {
  property: string
  source: string | null
  target: string | null
}

export interface ObjectDetail {
  summary: ObjectSummary
  properties: PropertyDto[]
  children: ObjectSummary[]
  childProperties: PropertyDto[]
  sourceDefinition: string | null
  targetDefinition: string | null
  plannedStatements: string[]
}

export interface StepDto {
  phase: string
  description: string
  sql: string
}

export interface ScriptResponse {
  sql: string
  stepCount: number
  byteSize: number
  exceedsReviewableSize: boolean
  steps: StepDto[]
}

async function post<T>(url: string, body: unknown): Promise<T> {
  const response = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })

  if (!response.ok) {
    throw new Error(await readError(response))
  }

  return (await response.json()) as T
}

async function readError(response: Response): Promise<string> {
  try {
    const problem = await response.json()
    return problem.detail ?? problem.title ?? `Request failed (${response.status})`
  } catch {
    return `Request failed (${response.status})`
  }
}

export const api = {
  probe: (connection: ConnectionRequest) => post<ProbeResponse>('/api/probe', connection),
  compare: (source: ConnectionRequest, target: ConnectionRequest) =>
    post<CompareResponse>('/api/compare', { source, target }),
  detail: async (compareId: string, objectId: string) => {
    const response = await fetch(`/api/compare/${compareId}/objects/${objectId}`)
    if (!response.ok) throw new Error(await readError(response))
    return (await response.json()) as ObjectDetail
  },
  script: (compareId: string, include: string[]) =>
    post<ScriptResponse>(`/api/compare/${compareId}/script`, { include }),
}

export interface ApplyResponse {
  outcome: 'Committed' | 'RolledBack' | 'Blocked' | 'Drifted'
  message: string
  stepCount: number
  durationMs: number
  serverMessage: string | null
  errorNumber: number | null
  blockers: string[]
  destructiveSteps: string[]
}

export interface RunLogEntryDto {
  id: string
  at: string
  action: string
  route: string
  outcome: string
  stepCount: number
  durationMs: number
  serverMessage: string | null
}

export const applyApi = {
  apply: (compareId: string, include: string[], confirmation: string, allowDestructive: boolean) =>
    postJson<ApplyResponse>(`/api/compare/${compareId}/apply`, { include, confirmation, allowDestructive }),
  runs: async () => {
    const response = await fetch('/api/runs')
    if (!response.ok) throw new Error('Could not read the run log')
    return (await response.json()) as RunLogEntryDto[]
  },
}

async function postJson<T>(url: string, body: unknown): Promise<T> {
  const response = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
  if (!response.ok) throw new Error(await readError(response))
  return (await response.json()) as T
}

export interface TableRow {
  qualifiedName: string
  sourceRows: number
  sourceBytes: number
  targetRows: number
  targetBytes: number
  keyColumns: string[]
  hasKey: boolean
  onBothSides: boolean
}

export interface VolumeSummary {
  sourceDataBytes: number
  sourceLogBytes: number
  sourceRows: number
  targetDataBytes: number
  targetLogBytes: number
  targetRows: number
  tables: TableRow[]
}

export interface CellDiff {
  column: string
  source: string | null
  target: string | null
}

export interface RowDiffDto {
  key: string
  display: string
  classification: 'Insert' | 'Update' | 'Delete'
  changes: CellDiff[]
  unchangedColumns: number
}

export type TableDataMode = 'SchemaOnly' | 'AllRows' | 'TopN' | 'Filter'

export interface DataCompareResponse {
  table: string
  mode: TableDataMode
  deletesSuppressed: boolean
  keyColumns: string[]
  comparedColumns: string[]
  excludedColumns: CellDiff[]
  insertCount: number
  updateCount: number
  deleteCount: number
  sameCount: number
  transferBytesEstimate: number
  footprintBytes: number
  rows: RowDiffDto[]
  warning: string | null
}

export const dataApi = {
  volume: async (compareId: string) => {
    const response = await fetch(`/api/compare/${compareId}/volume`)
    if (!response.ok) throw new Error('Could not read database sizes')
    return (await response.json()) as VolumeSummary
  },
  compare: (compareId: string, table: string, mode: TableDataMode, topCount: number, filter: string | null) =>
    postJson<DataCompareResponse>(`/api/compare/${compareId}/data`, { table, mode, topCount, filter }),
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} kB`
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`
  return `${(bytes / 1024 / 1024 / 1024).toFixed(1)} GB`
}

export interface FkNode {
  qualifiedName: string
  band: number
  state: string
  note: string
  rows: number
  bytes: number
}

export interface FkEdge {
  from: string
  to: string
  name: string
  columns: string
  nullable: boolean
  partOfCycle: boolean
}

export interface FkMapResponse {
  focus: string
  depth: number
  nodes: FkNode[]
  edges: FkEdge[]
  cycles: string[]
  notes: string[]
}

export const fkApi = {
  map: async (compareId: string, table: string, depth: number) => {
    const response = await fetch(
      `/api/compare/${compareId}/fk?table=${encodeURIComponent(table)}&depth=${depth}`,
    )
    if (!response.ok) throw new Error(await readError(response))
    return (await response.json()) as FkMapResponse
  },
}
