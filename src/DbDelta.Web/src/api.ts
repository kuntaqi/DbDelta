export type EnvironmentClass = 0 | 1 | 2 | 3

export const environmentName = ['Unknown', 'Dev', 'UAT', 'Prod'] as const

export type AuthMode = 'Windows' | 'SqlLogin'

export interface ConnectionRequest {
  connectionString?: string | null
  server?: string | null
  port?: number | null
  database?: string | null
  authentication?: AuthMode | null
  username?: string | null
  password?: string | null
  trustServerCertificate?: boolean
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
  warnings: string[]
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
  warnings: string[]
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
  destructive: boolean
}

// An object the plan carries because something ticked needs it, not because it was ticked. Both halves
// matter: without the reason, a plan that grew on your behalf cannot be argued with.
export interface RequiredObject {
  id: string
  type: string
  qualifiedName: string
  requiredBy: string
  reason: string
}

// Rows nobody picked: the parents a picked row points at. Counted per foreign key, because "3 rows of
// dbo.Category" says nothing about why they are in the plan.
export interface RequiredRows {
  table: string
  rowCount: number
  requiredBy: string
  foreignKeyName: string
}

// A table whose rows travel beside the script instead of inside it. The script stays readable; the rows
// come down as a data file in the same zip.
export interface StagedTable {
  table: string
  rowCount: number
  dataFileName: string
}

export interface ScriptResponse {
  sql: string
  stepCount: number
  byteSize: number
  exceedsReviewableSize: boolean
  steps: StepDto[]
  deleteWarnings: string[]
  required: RequiredObject[]
  unsatisfiable: string[]
  requiredRows: RequiredRows[]
  excluded: ExcludedObject[]
  staged: StagedTable[]
  narrowings: string[]
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
  script: (compareId: string) => post<ScriptResponse>(`/api/compare/${compareId}/script`, {}),
  // Before the script rather than after it: building one can be minutes of fetching rows, this is one
  // round trip for the row counts.
  estimate: async (compareId: string) => {
    const response = await fetch(`/api/compare/${compareId}/estimate`)
    if (!response.ok) throw new Error(await readError(response))
    return (await response.json()) as PlanEstimateResponse
  },
}

export interface PlanEstimateResponse {
  schemaBytes: number
  minBytes: number
  maxBytes: number
  minRows: number
  maxRows: number
  tables: number
  tablesNotScanned: number
  rowsAreExact: boolean
  verdict: 'Within' | 'Possibly' | 'Exceeds'
  reviewableLimitBytes: number
  notes: string[]
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

// The non-secret half of a connection, kept so it does not have to be retyped. Under a SQL login the
// password is still asked for every session: it is deliberately not in here.
export interface ConnectionProfile {
  name: string
  server: string
  port: number | null
  database: string
  authentication: AuthMode
  username: string | null
  trustServerCertificate: boolean
}

export const profileApi = {
  list: async () => {
    const response = await fetch('/api/profiles')
    if (!response.ok) throw new Error('Could not read the saved profiles')
    return (await response.json()) as ConnectionProfile[]
  },
  save: (profile: { name: string } & ConnectionRequest) =>
    postJson<ConnectionProfile[]>('/api/profiles', profile),
  remove: async (name: string) => {
    const response = await fetch('/api/profiles/' + encodeURIComponent(name), { method: 'DELETE' })
    if (!response.ok) throw new Error('Could not delete that profile')
    return (await response.json()) as ConnectionProfile[]
  },
}

export interface ComparedEndpoint {
  server: string
  port: number | null
  database: string
  authentication: AuthMode
  username: string | null
  trustServerCertificate: boolean
}

// A pair that was compared, and compared successfully. No password: the shape has nowhere to put one.
export interface ComparedPair {
  source: ComparedEndpoint
  target: ComparedEndpoint
  at: string
}

export const recentApi = {
  list: async () => {
    const response = await fetch('/api/recent')
    if (!response.ok) throw new Error('Could not read the recent pairs')
    return (await response.json()) as ComparedPair[]
  },
  forget: async () => {
    const response = await fetch('/api/recent', { method: 'DELETE' })
    if (!response.ok) throw new Error('Could not clear the recent pairs')
    return (await response.json()) as ComparedPair[]
  },
}

export interface InstanceDatabase {
  name: string
  state: string
  recoveryModel: string
  dataBytes: number
  logBytes: number
  isReadOnly: boolean
  accessible: boolean
  collation: string | null
  tables: number | null
  views: number | null
  routines: number | null
  problem: string | null
}

export interface InstanceSurveyResponse {
  server: string
  environment: EnvironmentClass
  readOnly: boolean
  databases: InstanceDatabase[]
  warning: string | null
}

export interface DatabasePairing {
  source: string
  target: string
}

export interface DatabasePair {
  name: string
  kind: 'ByName' | 'Declared' | 'SourceOnly' | 'TargetOnly'
  canBeCompared: boolean
  signal: 'SourceOnly' | 'TargetOnly' | 'Unreadable' | 'NotCompared' | 'CollationDiffers' | 'CountsDiffer' | 'CountsMatch'
  detail: string
  source: InstanceDatabase | null
  target: InstanceDatabase | null
}

export interface InstanceComparisonResponse {
  sourceServer: string
  targetServer: string
  sourceEnvironment: EnvironmentClass
  targetEnvironment: EnvironmentClass
  targetReadOnly: boolean
  durationMs: number
  onBothSides: number
  sourceOnly: number
  targetOnly: number
  described: boolean
  pairs: DatabasePair[]
  warnings: string[]
}

export const instanceApi = {
  survey: (connection: ConnectionRequest) =>
    postJson<InstanceSurveyResponse>('/api/instance', connection),
  // An empty list means every database; naming them describes only those.
  describe: (connection: ConnectionRequest, databases: string[]) =>
    postJson<InstanceSurveyResponse>('/api/instance/describe', { connection, databases }),
  // Describe is the expensive half: a connection per database per side, so it is asked for separately.
  compare: (
    source: ConnectionRequest,
    target: ConnectionRequest,
    pairings: DatabasePairing[],
    describe: boolean,
  ) =>
    postJson<InstanceComparisonResponse>('/api/instance/compare', {
      source,
      target,
      pairings,
      describe,
    }),
}

export const applyApi = {
  apply: (compareId: string, confirmation: string, allowDestructive: boolean) =>
    postJson<ApplyResponse>(`/api/compare/${compareId}/apply`, { confirmation, allowDestructive }),
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
  declaredKey: string[]
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
  direction: FkDirection
  nodes: FkNode[]
  edges: FkEdge[]
  cycles: string[]
  notes: string[]
}

export type FkDirection = 'Both' | 'Parents' | 'Children'

export const fkApi = {
  map: async (compareId: string, table: string, depth: number, direction: FkDirection) => {
    const response = await fetch(
      `/api/compare/${compareId}/fk?table=${encodeURIComponent(table)}&depth=${depth}&direction=${direction}`,
    )
    if (!response.ok) throw new Error(await readError(response))
    return (await response.json()) as FkMapResponse
  },
}

export interface SelectedTable {
  table: string
  mode: string
  topCount: number
  // null means the whole table. A list means those rows and no others, empty included.
  pickedRows: string[] | null
}

export const planApi = {
  select: (
    compareId: string,
    table: string,
    selected: boolean,
    mode: TableDataMode,
    topCount: number,
    filter: string | null,
    rows: string[] | null = null,
  ) =>
    postJson<{ selected: SelectedTable[] }>(`/api/compare/${compareId}/data/select`, {
      table,
      selected,
      mode,
      topCount,
      filter,
      rows,
    }),
  selection: async (compareId: string) => {
    const response = await fetch(`/api/compare/${compareId}/data/select`)
    if (!response.ok) throw new Error('Could not read the data selection')
    return (await response.json()) as { selected: SelectedTable[] }
  },
}

export interface TableScanRow {
  table: string
  comparable: boolean
  reason: string | null
  differs: boolean
  sourceRows: number
  targetRows: number
  rowDelta: number
}

export interface TableScanResponse {
  durationMs: number
  scanned: number
  differing: number
  notComparable: number
  skipped: number
  maxTableBytes: number
  tables: TableScanRow[]
}

export const scanApi = {
  run: (compareId: string, maxTableBytes: number) =>
    postJson<TableScanResponse>(
      `/api/compare/${compareId}/data/scan?maxTableBytes=${maxTableBytes}`,
      {},
    ),
  cached: async (compareId: string) => {
    const response = await fetch(`/api/compare/${compareId}/data/scan`)
    if (response.status === 204) return null
    if (!response.ok) throw new Error('Could not read the scan')
    return (await response.json()) as TableScanResponse
  },
}

// A refusal that was recorded rather than left unsaid. Under a database-wide plan there is no list to
// take an object off, so declining one has to be a first-class entry — and revocable.
export interface ExcludedObject {
  id: string
  qualifiedName: string
  kind: string
  reason: string
}

export type SelectionScope = 'Picked' | 'Database'

export interface SchemaSelectionResponse {
  selected: string[]
  differing: number
  dataTables: number
  required: RequiredObject[]
  unsatisfiable: string[]
  scope: SelectionScope
  excluded: ExcludedObject[]
}

export const schemaApi = {
  select: (compareId: string, objectId: string, selected: boolean) =>
    postJson<SchemaSelectionResponse>(`/api/compare/${compareId}/schema/select`, { objectId, selected }),
  scope: (compareId: string, scope: SelectionScope) =>
    postJson<SchemaSelectionResponse>(`/api/compare/${compareId}/schema/scope`, { scope }),
  clear: (compareId: string) =>
    postJson<SchemaSelectionResponse>(`/api/compare/${compareId}/schema/clear`, {}),
  selection: async (compareId: string) => {
    const response = await fetch(`/api/compare/${compareId}/schema/select`)
    if (!response.ok) throw new Error('Could not read the schema selection')
    return (await response.json()) as SchemaSelectionResponse
  },
}

export interface KeyCandidate {
  column: string
  dataType: string
  nullable: boolean
  declaredBy: string | null
  unique: boolean | null
  distinctValues: number
  note: string
}

export interface KeyChoiceResponse {
  table: string
  chosen: string[]
  fromPrimaryKey: boolean
  recommended: string[]
  rowCount: number
  probed: boolean
  candidates: KeyCandidate[]
  problem: string | null
  rejected: boolean
}

export const keyApi = {
  options: async (compareId: string, table: string) => {
    const response = await fetch(
      `/api/compare/${compareId}/data/key?table=${encodeURIComponent(table)}`,
    )
    if (!response.ok) throw new Error(await readError(response))
    return (await response.json()) as KeyChoiceResponse
  },
  choose: (compareId: string, table: string, columns: string[]) =>
    postJson<KeyChoiceResponse>(`/api/compare/${compareId}/data/key`, { table, columns }),
}
