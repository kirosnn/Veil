export type TurnRole = "user" | "assistant";

export interface ConversationTurn {
  role: TurnRole;
  content: string;
}

export interface SessionSummary {
  id: string;
  title: string;
  provider: string;
  model: string;
  preview: string;
  updatedAtLabel: string;
  selected: boolean;
}

export interface ModelOption {
  id: string;
  label: string;
  badge: string;
  selected: boolean;
}

export interface HostAppState {
  providerLabel: string;
  providerStatus: string;
  providerStatusTone: "valid" | "warning" | "invalid";
  activeConversationId: string | null;
  busy: boolean;
  canDelete: boolean;
  canCancel: boolean;
  sidebarVisible: boolean;
  currentModel: string;
  draftPrompt: string;
  draftVersion: number;
  turns: ConversationTurn[];
  sessions: SessionSummary[];
  modelOptions: ModelOption[];
}

export interface HostStateEnvelope {
  type: "hostState";
  state: HostAppState;
}

export type HostCommand =
  | { command: "ready" }
  | { command: "newChat" }
  | { command: "deleteChat" }
  | { command: "loadConversation"; sessionId: string }
  | { command: "toggleSidebar" }
  | { command: "selectModel"; modelId: string }
  | { command: "refreshModels" }
  | { command: "submitPrompt"; prompt: string }
  | { command: "cancelRequest" }
  | { command: "openFinder" };

export type WorkerInput =
  | { type: "init" }
  | { type: "hostState"; state: HostAppState }
  | { type: "draftChanged"; value: string }
  | { type: "submit" }
  | { type: "newChat" }
  | { type: "deleteChat" }
  | { type: "loadConversation"; sessionId: string }
  | { type: "toggleSidebar" }
  | { type: "toggleModelMenu" }
  | { type: "selectModel"; modelId: string }
  | { type: "refreshModels" }
  | { type: "cancelRequest" }
  | { type: "openFinder" };

export interface WorkerViewState {
  ready: boolean;
  sidebarVisible: boolean;
  providerLabel: string;
  providerStatus: string;
  providerStatusTone: "valid" | "warning" | "invalid";
  currentModelLabel: string;
  modelMenuOpen: boolean;
  modelOptions: ModelOption[];
  sessions: SessionSummary[];
  activeConversationId: string | null;
  turns: ConversationTurn[];
  showEmptyState: boolean;
  draft: string;
  busy: boolean;
  canDelete: boolean;
  canCancel: boolean;
  statusText: string;
}

export type WorkerOutput =
  | { type: "render"; view: WorkerViewState }
  | { type: "hostCommand"; payload: HostCommand };
