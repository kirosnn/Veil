import type { HostAppState, HostCommand } from "./types.js";

function cloneState(state: HostAppState): HostAppState {
  return {
    ...state,
    turns: state.turns.map((turn) => ({ ...turn })),
    sessions: state.sessions.map((session) => ({ ...session })),
    modelOptions: state.modelOptions.map((option) => ({ ...option }))
  };
}

export class MockHostTransport {
  private state: HostAppState = {
    providerLabel: "Mock OpenAI",
    providerStatus: "Mock mode for browser preview",
    providerStatusTone: "warning",
    activeConversationId: "demo",
    busy: false,
    canDelete: true,
    canCancel: false,
    sidebarVisible: true,
    currentModel: "gpt-5.4",
    draftPrompt: "",
    draftVersion: 0,
    turns: [
      {
        role: "assistant",
        content: "This standalone preview mirrors the embedded Finder AI webview without the native bridge."
      }
    ],
    sessions: [
      {
        id: "demo",
        title: "Embedded TypeScript prototype",
        provider: "Mock OpenAI",
        model: "gpt-5.4",
        preview: "This standalone preview mirrors the embedded Finder AI webview.",
        updatedAtLabel: "just now",
        selected: true
      }
    ],
    modelOptions: [
      { id: "gpt-5.4", label: "gpt-5.4", badge: "current", selected: true },
      { id: "gpt-5.4-mini", label: "gpt-5.4-mini", badge: "", selected: false },
      { id: "o4-mini", label: "o4-mini", badge: "", selected: false }
    ]
  };

  constructor(private readonly onState: (state: HostAppState) => void) {}

  dispatch(command: HostCommand): void {
    switch (command.command) {
      case "ready":
        this.pushState();
        break;
      case "newChat":
        this.state = {
          ...this.state,
          activeConversationId: null,
          turns: [],
          canDelete: false,
          draftPrompt: "",
          draftVersion: this.state.draftVersion + 1,
          sessions: this.state.sessions.map((session) => ({ ...session, selected: false }))
        };
        this.pushState();
        break;
      case "deleteChat":
        this.state = {
          ...this.state,
          activeConversationId: null,
          turns: [],
          canDelete: false,
          draftPrompt: "",
          draftVersion: this.state.draftVersion + 1,
          sessions: []
        };
        this.pushState();
        break;
      case "loadConversation":
        this.state = {
          ...this.state,
          activeConversationId: command.sessionId,
          turns: this.state.turns,
          canDelete: true,
          sessions: this.state.sessions.map((session) => ({
            ...session,
            selected: session.id === command.sessionId
          }))
        };
        this.pushState();
        break;
      case "toggleSidebar":
        this.state = {
          ...this.state,
          sidebarVisible: !this.state.sidebarVisible
        };
        this.pushState();
        break;
      case "selectModel":
        this.state = {
          ...this.state,
          currentModel: command.modelId,
          modelOptions: this.state.modelOptions.map((option) => ({
            ...option,
            selected: option.id === command.modelId
          }))
        };
        this.pushState();
        break;
      case "refreshModels":
        this.pushState();
        break;
      case "submitPrompt":
        this.handleSubmit(command.prompt);
        break;
      case "cancelRequest":
        this.state = {
          ...this.state,
          busy: false,
          canCancel: false,
          providerStatus: "Mock request cancelled",
          providerStatusTone: "warning"
        };
        this.pushState();
        break;
      case "openFinder":
        this.state = {
          ...this.state,
          providerStatus: "Back action triggered in mock mode",
          providerStatusTone: "warning"
        };
        this.pushState();
        break;
    }
  }

  private handleSubmit(prompt: string): void {
    const userTurn = { role: "user" as const, content: prompt };
    this.state = {
      ...this.state,
      busy: true,
      canCancel: true,
      activeConversationId: this.state.activeConversationId ?? "demo",
      canDelete: true,
      turns: [...this.state.turns, userTurn],
      sessions: [
        {
          id: this.state.activeConversationId ?? "demo",
          title: prompt.slice(0, 44) || "New conversation",
          provider: this.state.providerLabel,
          model: this.state.currentModel,
          preview: prompt,
          updatedAtLabel: "just now",
          selected: true
        }
      ]
    };
    this.pushState();

    window.setTimeout(() => {
      this.state = {
        ...this.state,
        busy: false,
        canCancel: false,
        providerStatus: "Mock mode for browser preview",
        providerStatusTone: "warning",
        turns: [
          ...this.state.turns,
          {
            role: "assistant",
            content: `Mock reply for: ${prompt}`
          }
        ]
      };
      this.pushState();
    }, 700);
  }

  private pushState(): void {
    this.onState(cloneState(this.state));
  }
}
