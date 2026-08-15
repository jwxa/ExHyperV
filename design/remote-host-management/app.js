const hosts = {
  local: {
    name: "本地计算机", address: "JWXA-PC", os: "Windows 11 专业版", status: "connected",
    badge: "已连接", identity: "当前 Windows 身份", icon: "local", credential: "当前 Windows 身份"
  },
  lab: {
    name: "LAB-HV-06", address: "10.0.0.6", os: "Windows 11 专业版", status: "partial",
    badge: "部分可用", identity: "JWXA\\Administrator", icon: "remote", credential: "未单独保存"
  },
  server: {
    name: "SERVER-2025", address: "10.0.0.20", os: "Windows Server 2025", status: "connected",
    badge: "已连接", identity: "LAB\\HyperVAdmin", icon: "remote", credential: "Windows 凭据管理器"
  },
  dev: {
    name: "DEV-HYPERV", address: "10.0.0.31", os: "Windows 11 专业版", status: "reconnecting",
    badge: "正在重连", identity: "JWXA\\Administrator", icon: "remote", credential: "未单独保存"
  }
};

const appWindow = document.querySelector("#app-window");
const layoutButtons = [...document.querySelectorAll("[data-layout]")];
const stateSwitcher = document.querySelector("#state-switcher");
const toast = document.querySelector("#toast");
const toastMessage = document.querySelector("#toast-message");
let selectedHostKey = "lab";
let activeHostKey = "lab";
let toastTimer;

function showToast(message) {
  toastMessage.textContent = message;
  toast.classList.add("show");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => toast.classList.remove("show"), 2400);
}

function setLayout(layout) {
  const safeLayout = ["a", "b", "c"].includes(layout) ? layout : "a";
  appWindow.classList.remove("layout-a", "layout-b", "layout-c");
  appWindow.classList.add(`layout-${safeLayout}`);
  layoutButtons.forEach(button => button.classList.toggle("active", button.dataset.layout === safeLayout));
  const url = new URL(window.location.href);
  url.searchParams.set("layout", safeLayout.toUpperCase());
  history.replaceState({}, "", url);
}

function statusPresentation(status) {
  if (status === "connected") {
    return {
      badgeClass: "success", badge: "已连接", bannerClass: "success", bannerIcon: "&#xE73E;",
      title: "远程管理与虚拟机控制台均可用",
      copy: "WMI/DCOM 与 TCP 2179 检测均通过。所有已支持的远程功能已启用。"
    };
  }
  if (status === "reconnecting") {
    return {
      badgeClass: "neutral", badge: "正在重连", bannerClass: "neutral", bannerIcon: "&#xE895;",
      title: "连接已中断，正在自动重连",
      copy: "当前内容标记为旧数据，写操作已禁用。第 2 次重试将在 12 秒后开始。"
    };
  }
  return {
    badgeClass: "warning", badge: "部分可用", bannerClass: "warning", bannerIcon: "&#xE7BA;",
    title: "管理功能可用，虚拟机控制台不可用",
    copy: "WMI/DCOM 已连接；TCP 2179 无法访问。你仍可管理虚拟机，但“打开控制台”会保持禁用。"
  };
}

function applyStatus(status, scope = "selected") {
  const presentation = statusPresentation(status);
  const banner = document.querySelector("#state-banner");
  const selectedBadge = document.querySelector("#selected-host-badge");
  const staleLabel = document.querySelector("#stale-label");
  selectedBadge.className = `status-badge ${presentation.badgeClass}`;
  selectedBadge.innerHTML = `<i></i>${presentation.badge}`;
  banner.className = `state-banner ${presentation.bannerClass}`;
  banner.querySelector(".banner-icon").innerHTML = presentation.bannerIcon;
  document.querySelector("#banner-title").textContent = presentation.title;
  document.querySelector("#banner-copy").textContent = presentation.copy;
  banner.querySelector(".text-button").hidden = status === "connected";
  staleLabel.textContent = status === "reconnecting" ? "旧数据 · 10:42:18" : "实时数据";
  staleLabel.classList.toggle("stale", status === "reconnecting");
  document.querySelector("#connect-button").disabled = status === "reconnecting";

  if (scope === "active") {
    const activeBadge = document.querySelector("#active-host-badge");
    activeBadge.className = `status-badge ${presentation.badgeClass}`;
    activeBadge.innerHTML = `<i></i>${presentation.badge}`;
  }
}

function selectHost(key) {
  const host = hosts[key];
  if (!host) return;
  selectedHostKey = key;
  document.querySelectorAll(".host-item").forEach(item => item.classList.toggle("selected", item.dataset.host === key));
  document.querySelector("#selected-host-name").textContent = host.name;
  document.querySelector("#selected-host-address").textContent = host.address;
  document.querySelector("#selected-host-os").textContent = host.os;
  document.querySelector("#property-host-name").textContent = host.name === "本地计算机" ? "JWXA-PC" : host.name;
  document.querySelector("#property-host-ip").textContent = host.address;
  document.querySelector("#property-host-os").textContent = host.os.replace("专业版", "24H2");
  document.querySelector("#property-credential").textContent = host.credential;
  document.querySelector("#connect-target").textContent = `${host.name} · ${host.address}`;
  document.querySelector("#wizard-host-label").textContent = `${host.name} · ${host.address}`;
  applyStatus(host.status);
}

function activateSelectedHost() {
  const host = hosts[selectedHostKey];
  activeHostKey = selectedHostKey;
  document.querySelector("#active-host-name").textContent = host.name;
  document.querySelector("#active-host-address").textContent = host.address;
  document.querySelector("#active-identity").textContent = host.identity;
  document.querySelector("#active-host-mark").className = `host-mark ${host.icon}`;
  applyStatus(host.status, "active");
  stateSwitcher.value = host.status;
  showToast(`活动主机已切换为 ${host.name}`);
}

layoutButtons.forEach(button => button.addEventListener("click", () => setLayout(button.dataset.layout)));
document.querySelectorAll(".host-item").forEach(item => item.addEventListener("click", () => selectHost(item.dataset.host)));

stateSwitcher.addEventListener("change", () => {
  hosts[selectedHostKey].status = stateSwitcher.value;
  applyStatus(stateSwitcher.value);
  if (selectedHostKey === activeHostKey) applyStatus(stateSwitcher.value, "active");
});

document.querySelectorAll(".tab").forEach(tab => {
  tab.addEventListener("click", () => {
    document.querySelectorAll(".tab").forEach(item => item.classList.toggle("active", item === tab));
    document.querySelectorAll(".tab-content").forEach(panel => panel.classList.toggle("active", panel.dataset.panel === tab.dataset.tab));
  });
});

function openDialog(selector) {
  const dialog = document.querySelector(selector);
  if (!dialog.open) dialog.showModal();
}

document.querySelectorAll("#add-host, #add-host-inline").forEach(button => button.addEventListener("click", () => openDialog("#add-host-dialog")));
document.querySelector("#connect-button").addEventListener("click", () => openDialog("#connect-dialog"));
document.querySelector("#confirm-connect").addEventListener("click", activateSelectedHost);
document.querySelector("#open-logs").addEventListener("click", () => document.querySelector('[data-tab="logs"]').click());
document.querySelector("#diagnose-button").addEventListener("click", () => document.querySelector('[data-tab="diagnostics"]').click());

document.querySelectorAll('input[name="credential-mode"]').forEach(input => {
  input.addEventListener("change", () => {
    document.querySelector(".explicit-fields").hidden = input.value !== "explicit" || !input.checked;
  });
});

document.querySelector("#add-host-form").addEventListener("submit", event => {
  if (event.submitter?.value !== "default") return;
  event.preventDefault();
  document.querySelector("#add-host-dialog").close();
  document.querySelector('[data-tab="diagnostics"]').click();
  showToast("主机配置已保存，连接检测已开始");
});

document.querySelector("#host-search").addEventListener("input", event => {
  const query = event.target.value.trim().toLowerCase();
  document.querySelectorAll(".host-item").forEach(item => {
    item.hidden = !item.textContent.toLowerCase().includes(query);
  });
});

document.querySelector("#refresh-hosts").addEventListener("click", () => showToast("主机状态已刷新"));
document.querySelector("#run-diagnostics").addEventListener("click", event => {
  event.currentTarget.disabled = true;
  event.currentTarget.innerHTML = '<span class="fluent-icon">&#xE895;</span>检测中';
  const log = document.querySelector("#diagnostic-log");
  log.textContent = "[10:48:03.102] [检测] 正在重新检测 IPv4、身份、WMI/DCOM 与 TCP 2179...";
  setTimeout(() => {
    event.currentTarget.disabled = false;
    event.currentTarget.innerHTML = '<span class="fluent-icon">&#xE9D9;</span>开始检测';
    log.textContent += "\n[10:48:04.228] [结果] WMI/DCOM 正常，TCP 2179 仍不可访问";
    showToast("检测完成：主机部分可用");
  }, 900);
});

const assistantDialog = document.querySelector("#assistant-dialog");
const wizardStepNames = ["detect", "account", "network", "confirm"];
let wizardStep = 0;

function showWizardStep(index) {
  wizardStep = Math.max(0, Math.min(index, wizardStepNames.length - 1));
  const name = wizardStepNames[wizardStep];
  document.querySelectorAll("[data-step]").forEach(button => button.classList.toggle("active", button.dataset.step === name));
  document.querySelectorAll("[data-step-panel]").forEach(panel => panel.classList.toggle("active", panel.dataset.stepPanel === name));
  document.querySelector("#wizard-back").disabled = wizardStep === 0;
  document.querySelector("#wizard-step-number").textContent = String(wizardStep + 1);
  document.querySelector("#wizard-next").hidden = wizardStep === wizardStepNames.length - 1;
  document.querySelector("#apply-changes").hidden = wizardStep !== wizardStepNames.length - 1;
}

function openAssistant() {
  showWizardStep(0);
  if (!assistantDialog.open) assistantDialog.showModal();
}

document.querySelectorAll("#open-assistant, .open-assistant-link").forEach(button => button.addEventListener("click", openAssistant));
document.querySelector("#close-assistant").addEventListener("click", () => assistantDialog.close());
document.querySelector("#wizard-next").addEventListener("click", () => showWizardStep(wizardStep + 1));
document.querySelector("#wizard-back").addEventListener("click", () => showWizardStep(wizardStep - 1));
document.querySelectorAll("[data-step]").forEach((button, index) => button.addEventListener("click", () => showWizardStep(index)));

document.querySelectorAll('input[name="allowed-account"]').forEach((input, index) => {
  input.addEventListener("change", () => {
    document.querySelector(".domain-field").hidden = index !== 2 || !input.checked;
  });
});

const confirmationInput = document.querySelector("#confirmation-input");
const applyChangesButton = document.querySelector("#apply-changes");
confirmationInput.addEventListener("input", () => {
  applyChangesButton.disabled = confirmationInput.value !== "确认";
});

applyChangesButton.addEventListener("click", () => {
  applyChangesButton.disabled = true;
  applyChangesButton.textContent = "正在应用...";
  setTimeout(() => {
    document.querySelector("#apply-result").hidden = false;
    applyChangesButton.textContent = "完成";
    applyChangesButton.disabled = false;
    applyChangesButton.onclick = () => assistantDialog.close();
    hosts.lab.status = "connected";
    applyStatus("connected");
    if (activeHostKey === "lab") applyStatus("connected", "active");
    showToast("远程主机配置完成，回滚脚本已生成");
  }, 1100);
});

document.querySelector("#rerun-preflight").addEventListener("click", event => {
  event.currentTarget.disabled = true;
  event.currentTarget.textContent = "检测中...";
  setTimeout(() => {
    event.currentTarget.disabled = false;
    event.currentTarget.innerHTML = '<span class="fluent-icon">&#xE72C;</span>重新检测';
    showToast("预检完成，发现 2 项需要配置");
  }, 800);
});

document.querySelectorAll(".nav-item:not(.selected)").forEach(button => button.addEventListener("click", () => showToast(`${button.title} 页面不在本次原型范围内`)));

const initialUrl = new URL(window.location.href);
setLayout((initialUrl.searchParams.get("layout") || "A").toLowerCase());
const initialState = initialUrl.searchParams.get("state");
if (["partial", "connected", "reconnecting"].includes(initialState)) {
  stateSwitcher.value = initialState;
  hosts.lab.status = initialState;
  applyStatus(initialState);
  applyStatus(initialState, "active");
}
selectHost("lab");
if (initialUrl.searchParams.get("dialog") === "assistant") {
  window.addEventListener("load", openAssistant, { once: true });
}
