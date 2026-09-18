const openers = new WeakMap();
const initialized = new WeakSet();
let navigationReference;
let unsaved = false;
let busy = false;
let dialogObserver;
let modalStack = [];

function focusableElements(dialog) {
    return [...dialog.querySelectorAll('button, a[href], input, select, textarea, [tabindex]')]
        .filter(element => !element.matches(':disabled') && element.tabIndex >= 0
            && !element.closest('[inert]') && element.getClientRects().length > 0
            && getComputedStyle(element).visibility === 'visible');
}

function syncModals() {
    while (modalStack.length && !modalStack.at(-1).dialog.isConnected) {
        const closed = modalStack.pop();
        if (closed.opener?.isConnected) closed.opener.focus();
    }
    for (const dialog of document.querySelectorAll('.config-modal-backdrop .config-modal[role="dialog"]')) {
        if (modalStack.some(entry => entry.dialog === dialog)) continue;
        modalStack.push({ dialog, opener: document.activeElement });
        dialog.tabIndex = -1;
        if (!dialog.contains(document.activeElement)) {
            (dialog.querySelector('[data-dialog-cancel]') ?? focusableElements(dialog)[0] ?? dialog).focus();
        }
    }
}

function guardModalKeyboard(event) {
    if (document.querySelector('dialog[open]')) return;
    const dialog = modalStack.at(-1)?.dialog;
    if (!dialog?.isConnected) return;
    if (event.key === 'Escape') {
        event.preventDefault();
        event.stopImmediatePropagation();
        const cancel = dialog.querySelector('[data-dialog-cancel]');
        if (cancel && !cancel.disabled) cancel.click();
    } else if (event.key === 'Tab') {
        const controls = focusableElements(dialog);
        const current = controls.indexOf(document.activeElement);
        if (!controls.length || current < 0 || (event.shiftKey ? current === 0 : current === controls.length - 1)) {
            event.preventDefault();
            (event.shiftKey ? controls.at(-1) ?? dialog : controls[0] ?? dialog).focus();
        }
    }
}

function guardModalFocus(event) {
    if (document.querySelector('dialog[open]')) return;
    const dialog = modalStack.at(-1)?.dialog;
    if (dialog?.isConnected && !dialog.contains(event.target)) {
        (focusableElements(dialog)[0] ?? dialog).focus();
    }
}

function guardLink(event) {
    if ((!unsaved && !busy) || event.defaultPrevented || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
    const anchor = event.target.closest?.('a[href]');
    if (!anchor || anchor.hasAttribute('download') || (anchor.target && anchor.target !== '_self')) return;
    const target = new URL(anchor.href, location.href);
    if (target.origin !== location.origin || (target.pathname === location.pathname && target.search === location.search && target.hash)) return;
    event.preventDefault();
    event.stopImmediatePropagation();
    if (!busy) navigationReference?.invokeMethodAsync('RequestLeaveAsync', target.href);
}

function guardUnload(event) {
    if (!unsaved && !busy) return;
    event.preventDefault();
    event.returnValue = '';
}

export function setNavigationGuard(reference, hasChanges, isBusy) {
    navigationReference = reference;
    unsaved = hasChanges;
    busy = isBusy;
    document.addEventListener('click', guardLink, true);
    window.addEventListener('beforeunload', guardUnload);
    if (!dialogObserver) {
        dialogObserver = new MutationObserver(syncModals);
        dialogObserver.observe(document.body, { childList: true, subtree: true });
        document.addEventListener('keydown', guardModalKeyboard, true);
        document.addEventListener('focusin', guardModalFocus);
        syncModals();
    }
}

export function detachNavigationGuard() {
    document.removeEventListener('click', guardLink, true);
    window.removeEventListener('beforeunload', guardUnload);
    dialogObserver?.disconnect();
    dialogObserver = undefined;
    modalStack = [];
    document.removeEventListener('keydown', guardModalKeyboard, true);
    document.removeEventListener('focusin', guardModalFocus);
    navigationReference = undefined;
    unsaved = false;
    busy = false;
}

export function open(dialog) {
    if (dialog.open) return;
    if (!initialized.has(dialog)) {
        const cancelDialog = event => {
            event.preventDefault();
            const cancel = dialog.querySelector('[data-dialog-cancel]');
            if (cancel && !cancel.disabled) cancel.click();
        };
        dialog.addEventListener('cancel', cancelDialog);
        dialog.addEventListener('keydown', event => {
            if (event.key === 'Escape') cancelDialog(event);
        });
        initialized.add(dialog);
    }
    openers.set(dialog, document.activeElement);
    dialog.showModal();
    dialog.querySelector('[autofocus], button')?.focus();
}

export function close(dialog) {
    dialog.close();
    const opener = openers.get(dialog);
    if (opener?.isConnected) opener.focus();
    openers.delete(dialog);
}