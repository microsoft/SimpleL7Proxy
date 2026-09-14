window.chatScroll = {
    register: function (el) {
        if (!el || el._chatScrollRegistered) {
            return;
        }
        el._chatScrollRegistered = true;
        el._stick = true;
        el.addEventListener('scroll', function () {
            const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 24;
            el._stick = atBottom;
        });
    },
    scrollIfStuck: function (el) {
        if (!el) {
            return;
        }
        if (el._stick !== false) {
            el.scrollTop = el.scrollHeight;
        }
    }
};

window.chatComposer = {
    register: function (el, dotNetRef) {
        if (!el || el._chatComposerRegistered) {
            return;
        }

        el._chatComposerRegistered = true;
        el.addEventListener('keydown', function (event) {
            if (event.key !== 'Enter' || event.shiftKey) {
                return;
            }

            event.preventDefault();
            dotNetRef.invokeMethodAsync('SubmitComposerAsync');
        });
    }
};

window.errorPanel = {
    getScrollMetrics: function (el) {
        if (!el) { return null; }
        return { scrollTop: el.scrollTop || 0, clientHeight: el.clientHeight || 0, scrollHeight: el.scrollHeight || 0 };
    },

    autoScrollToNewest: function (el) {
        // Only scroll to "now" (left=0) if the user is already near the left edge.
        // If they've manually scrolled right to inspect older data, leave them alone.
        if (!el) { return; }
        const nearLeft = el.scrollLeft < 80;
        if (nearLeft) { el.scrollLeft = 0; }
    },

    scrollOutlineIntoView: function (scrollEl, leftRatio, widthRatio) {
        if (!scrollEl) {
            return;
        }

        const maxLeft = Math.max(0, scrollEl.scrollWidth - scrollEl.clientWidth);
        if (maxLeft <= 0) {
            return;
        }

        const outlineLeft = Math.max(0, Math.min(1, leftRatio || 0)) * scrollEl.scrollWidth;
        const outlineWidth = Math.max(0.01, Math.min(1, widthRatio || 0)) * scrollEl.scrollWidth;
        const outlineRight = outlineLeft + outlineWidth;
        const viewLeft = scrollEl.scrollLeft;
        const viewRight = viewLeft + scrollEl.clientWidth;
        const gutter = 16;

        let target = viewLeft;
        if (outlineLeft < viewLeft + gutter) {
            target = outlineLeft - gutter;
        } else if (outlineRight > viewRight - gutter) {
            target = outlineRight - scrollEl.clientWidth + gutter;
        }

        target = Math.max(0, Math.min(maxLeft, target));
        if (Math.abs(target - viewLeft) > 2) {
            scrollEl.scrollLeft = target;
        }
    }
};

window.probeTable = {
    _current: null,
    register: function (tableEl) {
        if (!tableEl || tableEl._probeTableRegistered) {
            return;
        }
        tableEl._probeTableRegistered = true;
        const self = this;

        tableEl.addEventListener('mouseover', function (e) {
            const row = e.target.closest('tr.result-row');
            if (!row || !tableEl.contains(row)) {
                return;
            }
            if (row === self._current) {
                return;
            }

            // hide previous panel
            if (self._current) {
                const prev = self._current.querySelector('.probe-summary-panel');
                if (prev) {
                    prev.style.display = 'none';
                }
            }
            self._current = row;

            const panel = row.querySelector('.probe-summary-panel');
            if (!panel) {
                return;
            }

            const rowRect = row.getBoundingClientRect();
            const vh = window.innerHeight;
            const vw = window.innerWidth;
            const panelW = 700;
            const panelMaxH = 460;

            // horizontal: align to right edge of row, clamped inside viewport
            let left = Math.round(rowRect.right) - panelW;
            left = Math.max(8, Math.min(left, vw - panelW - 8));

            // vertical: show below when there is room, above otherwise
            const spaceBelow = vh - rowRect.bottom - 8;
            const spaceAbove = rowRect.top - 8;
            let top;
            if (spaceBelow >= 180 || spaceBelow >= spaceAbove) {
                top = Math.round(rowRect.bottom) + 2;
            } else {
                const clampedH = Math.min(panelMaxH, spaceAbove);
                top = Math.round(rowRect.top) - clampedH - 2;
            }

            panel.style.cssText =
                'display:block; position:fixed; left:' + left + 'px; top:' + top + 'px;' +
                ' width:' + panelW + 'px; max-height:' + panelMaxH + 'px;';
        });

        tableEl.addEventListener('mouseleave', function () {
            if (self._current) {
                const panel = self._current.querySelector('.probe-summary-panel');
                if (panel) {
                    panel.style.display = 'none';
                }
                self._current = null;
            }
        });
    }
};

window.requestDetailsPopup = {
    _current: null,
    _pending: null,
    _armed: false,
    _hideTimer: null,
    _idleTimer: null,
    register: function (root) {
        if (!root || root._requestDetailsPopupRegistered) {
            return;
        }

        root._requestDetailsPopupRegistered = true;
        const self = this;

        const showPanel = function (row, event) {
            if (!row || !root.contains(row)) {
                return;
            }

            if (row === self._current) {
                return;
            }

            self.hideCurrent(false);
            self._current = row;

            const panel = row.querySelector('.request-details-panel');
            if (!panel) {
                return;
            }

            const rowRect = row.getBoundingClientRect();
            const vh = window.innerHeight;
            const vw = window.innerWidth;
            const panelW = Math.min(1120, vw - 16);
            const panelMaxH = 620;

            const left = Math.max(8, Math.round((vw - panelW) / 2));

            const placeBelow = (event?.clientY || rowRect.top) < vh / 2;
            let top;
            if (placeBelow) {
                top = Math.round(rowRect.bottom);
                top = Math.min(top, vh - Math.min(panelMaxH, vh - 16) - 8);
            } else {
                const availableAbove = Math.max(180, rowRect.top - 8);
                top = Math.round(rowRect.top) - Math.min(panelMaxH, availableAbove);
                top = Math.max(8, top);
            }

            panel.style.cssText =
                'display:block; position:fixed; left:' + left + 'px; top:' + top + 'px;' +
                ' width:' + panelW + 'px; max-height:' + panelMaxH + 'px;';
        };

        const queueShow = function (event) {
            self.resetIdleTimer();
            const row = event.target.closest('.request-detail-row');
            if (!row || !root.contains(row)) {
                return;
            }

            if (row === self._current) {
                return;
            }

            if (self._hideTimer) {
                window.clearTimeout(self._hideTimer);
                self._hideTimer = null;
            }

            if (self._armed) {
                showPanel(row, event);
                return;
            }

            if (self._pending) {
                window.clearTimeout(self._pending);
            }

            self._pending = window.setTimeout(function () {
                self._armed = true;
                showPanel(row, event);
                self._pending = null;
            }, 1500);
        };

        root.addEventListener('mouseover', queueShow);
        root.addEventListener('focusin', queueShow);
        root.addEventListener('click', function (event) {
            const closeButton = event.target.closest('.rdp-close');
            if (closeButton && root.contains(closeButton)) {
                event.preventDefault();
                event.stopPropagation();
                if (self._pending) {
                    window.clearTimeout(self._pending);
                    self._pending = null;
                }

                self.hideCurrent(true);
                return;
            }

            const row = event.target.closest('.request-detail-row');
            if (!row || !root.contains(row)) {
                return;
            }

            if (self._pending) {
                window.clearTimeout(self._pending);
                self._pending = null;
            }

            self._armed = true;
            showPanel(row, event);
            self.resetIdleTimer();
        });
        root.addEventListener('mouseout', function (event) {
            if (!self._current && !self._pending) {
                return;
            }

            const next = event.relatedTarget;
            const panel = self._current?.querySelector('.request-details-panel');
            if ((next && self._current?.contains(next)) || (panel && next && panel.contains(next))) {
                return;
            }

            self._hideTimer = window.setTimeout(function () {
                const active = document.elementFromPoint(window._requestDetailsLastX || 0, window._requestDetailsLastY || 0);
                if (active && (self._current?.contains(active) || panel?.contains(active))) {
                    return;
                }

                if (self._pending) {
                    window.clearTimeout(self._pending);
                    self._pending = null;
                }

                self.hideCurrent(true);
                self._hideTimer = null;
            }, 350);
        });

        document.addEventListener('mousemove', function (event) {
            window._requestDetailsLastX = event.clientX;
            window._requestDetailsLastY = event.clientY;
            self.resetIdleTimer();
        }, { passive: true });
    },
    resetIdleTimer: function () {
        if (this._idleTimer) {
            window.clearTimeout(this._idleTimer);
        }

        if (!this._current && !this._pending) {
            this._idleTimer = null;
            return;
        }

        const self = this;
        this._idleTimer = window.setTimeout(function () {
            if (self._pending) {
                window.clearTimeout(self._pending);
                self._pending = null;
            }

            self.hideCurrent(true);
            self._idleTimer = null;
        }, 30000);
    },
    hideCurrent: function (resetArmed) {
        if (!this._current) {
            if (resetArmed) {
                this._armed = false;
            }
            return;
        }

        const panel = this._current.querySelector('.request-details-panel');
        if (panel) {
            panel.style.display = 'none';
        }

        this._current = null;
        if (resetArmed) {
            this._armed = false;
        }

        if (!this._current && !this._pending && this._idleTimer) {
            window.clearTimeout(this._idleTimer);
            this._idleTimer = null;
        }
    }
};

window.chatResponseDetails = {
    register: function (root) {
        if (!root || root._chatResponseDetailsRegistered) {
            return;
        }

        root._chatResponseDetailsRegistered = true;
        const setActive = function (event) {
            const trigger = event.target.closest('.metrics-host, .headers-host, .raw-host');
            if (!trigger || !root.contains(trigger)) {
                return;
            }

            const bubble = trigger.closest('.assistant-bubble');
            if (!bubble) {
                return;
            }

            if (trigger.classList.contains('metrics-host')) {
                bubble.dataset.activeDetail = 'metrics';
                return;
            }

            bubble.dataset.activeDetail = trigger.classList.contains('headers-host') ? 'headers' : 'raw';

            const bubbleRect = bubble.getBoundingClientRect();
            const rootRect = root.getBoundingClientRect();
            const availableAbove = bubbleRect.top - rootRect.top;
            const expectedPanelHeight = Math.min(416, Math.max(220, root.clientHeight * 0.55));
            bubble.dataset.detailPlacement = availableAbove < expectedPanelHeight ? 'below' : 'above';
        };

        const clearActive = function (event) {
            if (!event.target.classList || !event.target.classList.contains('assistant-bubble')) {
                return;
            }

            if (root.contains(event.target)) {
                delete event.target.dataset.activeDetail;
            }
        };

        root.addEventListener('pointerenter', setActive, true);
        root.addEventListener('focusin', setActive, true);
        root.addEventListener('pointerleave', clearActive, true);
    }
};

window.responseSearch = (function () {
    const roots = [];
    let current = null;
    let listening = false;

    function register(root) {
        if (!root || root._responseSearchRegistered) {
            return;
        }

        root._responseSearchRegistered = true;
        root.setAttribute('tabindex', root.getAttribute('tabindex') || '0');
        root.addEventListener('mousedown', function () {
            window.setTimeout(function () {
                if (!isInteractive(document.activeElement) && document.body.contains(root)) {
                    root.focus({ preventScroll: true });
                }

                if (current && current.root !== root) {
                    switchRoot(root);
                }
            }, 0);
        });
        root.addEventListener('focusin', function () {
            if (current && current.root !== root) {
                switchRoot(root);
            }
        });
        roots.push(root);

        if (!listening) {
            document.addEventListener('keydown', onDocumentKeyDown, true);
            listening = true;
        }
    }

    function refresh() {
        if (!current) {
            return;
        }

        const query = current.query;
        if (!document.body.contains(current.root)) {
            const replacementRoot = findReplacementRoot(current);
            if (!replacementRoot) {
                return;
            }

            current.root = replacementRoot;
        }

        if (!document.body.contains(current.popup)) {
            const root = current.root;
            clearMarks(root);
            current = null;
            createPopup(root);
            current.input.value = query;
        }

        if (query) {
            current.input.value = query;
            applySearch(query);
        }
    }

    function onDocumentKeyDown(event) {
        const key = event.key.toLowerCase();
        if (!(event.ctrlKey || event.metaKey) || (key !== 'f' && key !== 's')) {
            return;
        }

        const root = findFocusedRoot();
        if (!root) {
            return;
        }

        event.preventDefault();
        event.stopPropagation();
        show(root);
    }

    function findFocusedRoot() {
        const active = document.activeElement;
        for (const root of roots) {
            if (root && document.body.contains(root) && (root === active || root.contains(active))) {
                return root;
            }
        }

        return null;
    }

    function show(root) {
        if (current && current.root !== root) {
            switchRoot(root);
            return;
        }

        if (!current) {
            createPopup(root);
        }

        current.input.focus();
        current.input.select();
    }

    function createPopup(root) {
        const host = root.closest('.search-host') || root.parentElement || root;
        const popup = document.createElement('div');
        popup.className = 'response-search-popup';

        const input = document.createElement('input');
        input.className = 'form-control form-control-sm';
        input.type = 'text';
        input.placeholder = 'Find';

        const status = document.createElement('span');
        status.className = 'response-search-status';
        status.textContent = '0/0';

        const previous = document.createElement('button');
        previous.className = 'btn btn-sm btn-outline-secondary';
        previous.type = 'button';
        previous.textContent = '<';
        previous.title = 'Previous match';

        const next = document.createElement('button');
        next.className = 'btn btn-sm btn-outline-secondary';
        next.type = 'button';
        next.textContent = '>';
        next.title = 'Next match';

        const closeButton = document.createElement('button');
        closeButton.className = 'btn btn-sm btn-outline-secondary';
        closeButton.type = 'button';
        closeButton.textContent = 'x';
        closeButton.title = 'Close search';

        popup.append(input, status, previous, next, closeButton);
        host.appendChild(popup);

        current = { root, popup, input, status, marks: [], index: -1, query: '' };

        input.addEventListener('input', function () {
            applySearch(input.value);
        });
        input.addEventListener('keydown', function (event) {
            if (event.key === 'ArrowDown' || event.key === 'Enter') {
                event.preventDefault();
                move(1);
            } else if (event.key === 'ArrowUp') {
                event.preventDefault();
                move(-1);
            } else if (event.key === 'Escape') {
                event.preventDefault();
                close(true);
            }
        });
        previous.addEventListener('click', function () { move(-1); });
        next.addEventListener('click', function () { move(1); });
        closeButton.addEventListener('click', function () { close(true); });

        return popup;
    }

    function switchRoot(root) {
        const query = current ? current.query : '';
        close(false);
        createPopup(root);
        current.input.value = query;
        applySearch(query);
        current.input.focus();
        current.input.select();
    }

    function applySearch(query) {
        if (!current) {
            return;
        }

        clearMarks(current.root);
        current.marks = [];
        current.index = -1;
        current.query = query;

        if (!query) {
            updateStatus();
            return;
        }

        const escapedQuery = escapeRegExp(query);
        const matcher = new RegExp(escapedQuery, 'gi');
        const lowerQuery = query.toLowerCase();
        const walker = document.createTreeWalker(current.root, NodeFilter.SHOW_TEXT, {
            acceptNode: function (node) {
                if (!node.nodeValue || !node.nodeValue.toLowerCase().includes(lowerQuery)) {
                    return NodeFilter.FILTER_REJECT;
                }
                if (current.popup.contains(node.parentElement)) {
                    return NodeFilter.FILTER_REJECT;
                }
                return NodeFilter.FILTER_ACCEPT;
            }
        });

        const nodes = [];
        while (walker.nextNode()) {
            nodes.push(walker.currentNode);
        }

        for (const node of nodes) {
            highlightNode(node, matcher);
        }

        if (current.marks.length > 0) {
            current.index = 0;
            activateCurrent();
        }

        updateStatus();
    }

    function highlightNode(node, matcher) {
        const text = node.nodeValue;
        const fragment = document.createDocumentFragment();
        let lastIndex = 0;
        matcher.lastIndex = 0;

        for (const match of text.matchAll(matcher)) {
            if (match.index > lastIndex) {
                fragment.appendChild(document.createTextNode(text.slice(lastIndex, match.index)));
            }

            const mark = document.createElement('mark');
            mark.className = 'response-search-match';
            mark.textContent = match[0];
            current.marks.push(mark);
            fragment.appendChild(mark);
            lastIndex = match.index + match[0].length;
        }

        if (lastIndex < text.length) {
            fragment.appendChild(document.createTextNode(text.slice(lastIndex)));
        }

        node.parentNode.replaceChild(fragment, node);
    }

    function move(delta) {
        if (!current || current.marks.length === 0) {
            updateStatus();
            return;
        }

        current.index = (current.index + delta + current.marks.length) % current.marks.length;
        activateCurrent();
        updateStatus();
    }

    function activateCurrent() {
        current.marks.forEach(function (mark) { mark.classList.remove('active'); });
        const active = current.marks[current.index];
        if (active) {
            active.classList.add('active');
            active.scrollIntoView({ block: 'center', inline: 'nearest' });
        }
    }

    function updateStatus() {
        if (!current) {
            return;
        }

        current.status.textContent = current.marks.length === 0 ? '0/0' : `${current.index + 1}/${current.marks.length}`;
    }

    function close(restoreFocus) {
        if (!current) {
            return;
        }

        const root = current.root;
        clearMarks(root);
        current.popup.remove();
        current = null;
        if (restoreFocus && root && document.body.contains(root)) {
            root.focus({ preventScroll: true });
        }
    }

    function findReplacementRoot(searchState) {
        const host = searchState.popup && document.body.contains(searchState.popup)
            ? searchState.popup.closest('.search-host')
            : null;
        if (!host) {
            return null;
        }

        for (let index = roots.length - 1; index >= 0; index--) {
            const root = roots[index];
            if (root && document.body.contains(root) && root.closest('.search-host') === host) {
                return root;
            }
        }

        return null;
    }

    function clearMarks(root) {
        root.querySelectorAll('mark.response-search-match').forEach(function (mark) {
            const text = document.createTextNode(mark.textContent || '');
            const parent = mark.parentNode;
            parent.replaceChild(text, mark);
            parent.normalize();
        });
    }

    function escapeRegExp(value) {
        return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    }

    function isInteractive(element) {
        return element && ['INPUT', 'TEXTAREA', 'SELECT', 'BUTTON'].includes(element.tagName);
    }

    return { register, refresh };
})();
