// The Host owns pending lifetime and first-answer-wins; native forms own browser selection.
import { h, mount, link, toast } from '../dom.js';
import { describe } from '../hub.js';
import { HostProtocol, Capability, PendingKind, ApprovalChoice, QuestionResult } from '/protocol.js';
import { sessionRoute, sessionTitle, emptyState, syncList, prettyJson } from './common.js';

export function pendingView(container, hub) {
  const list = h('div', { className: 'pending-list', id: 'pending-list' });
  const empty = emptyState('Nothing is waiting for an answer.');
  mount(container, h('h1', {}, 'Pending decisions'), empty, list);

  function update() {
    const items = hub.pending();
    empty.hidden = items.length > 0;
    syncPendingCards(list, hub, items);
  }

  update();
  return { update };
}

/** Preserve in-progress native inputs, but make removed cards inert even to retained DOM references. */
export function syncPendingCards(list, hub, items) {
  const present = new Set(items.map(pendingKey));
  for (const card of list.children) {
    if (!present.has(card.dataset.key))
      card.querySelectorAll('fieldset').forEach((fieldset) => { fieldset.disabled = true; });
  }
  syncList(list, items, pendingKey, (item) => pendingCard(hub, item));
}

function pendingKey(item) { return `${item.machine}/${item.request.RequestId}`; }

export function pendingCard(hub, item) {
  const machineLink = hub.link(item.machine);
  const session = machineLink?.sessions.get(item.request.SessionId);
  const isPending = () => hub.pending().some((current) => pendingKey(current) === pendingKey(item));
  const head = h('header', { className: 'pending-head' },
    h('span', { className: `chip kind-${item.kind}` }, item.kind === PendingKind.Approval ? 'Approval' : 'Question'),
    h('span', { className: 'muted' }, item.machine, ' · '),
    link(sessionRoute(item.machine, item.request.SessionId), {}, session ? sessionTitle(session) : item.request.SessionId));
  const body = item.kind === PendingKind.Approval
    ? approvalBody(machineLink, item.request, isPending)
    : questionBody(machineLink, item.request, isPending);
  return h('article', { className: `pending card ${item.kind}`, dataset: { request: item.request.RequestId, kind: item.kind } }, head, body);
}

function approvalBody(machineLink, request, isPending) {
  const canCancel = machineLink.supports(Capability.DecisionCancellation);
  const availability = request.Availability;
  const available = {
    once: !availability || availability.AllowOnce === true,
    session: !availability || availability.AllowForSession === true,
    reject: !availability || availability.Reject === true,
    cancel: canCancel,
  };
  const actions = Object.entries(ApprovalChoice).map(([value, label]) => h('button', {
    type: 'button',
    className: value === 'once' || value === 'session' ? 'primary' : value === 'reject' ? 'danger' : '',
    value,
    disabled: !available[value],
    title: !available[value] ? `${label} is unavailable for this request` : value === 'session' ? 'Allow this tool in the current session only' : label,
    onClick: () => answer(value),
  }, value === 'session' ? `${label} (this session)` : label));
  const fields = h('fieldset', {},
    h('div', { className: 'actions' }, actions));

  async function answer(value) {
    if (fields.disabled || !isPending() || !available[value])
      return;
    fields.disabled = true;
    try {
      const applied = await machineLink.request(HostProtocol.MethodApprovalRespond, {
        RequestId: request.RequestId,
        Allow: value === 'once' || value === 'session',
        Reason: null,
        RememberForSession: value === 'session',
        Cancelled: value === 'cancel',
      });
      if (!applied && isPending()) {
        toast('The Host did not apply this choice; the request is still pending.');
        fields.disabled = false;
      }
    } catch (error) {
      if (isPending()) {
        toast(`Could not answer: ${describe(error)}`, 'error');
        fields.disabled = false;
      }
    }
  }

  const argumentsText = prettyJson(request.Arguments);
  return h('div', {
    className: 'pending-body',
    onKeydown: (event) => {
      if (event.ctrlKey || event.altKey || event.metaKey)
        return;
      const value = ({ y: 'once', a: 'session', n: 'reject', c: 'cancel', Escape: 'cancel' })[event.key];
      if (value) {
        event.preventDefault();
        event.stopPropagation();
        void answer(value);
      }
    },
  },
  h('p', {}, h('span', { className: 'muted' }, 'Tool '), h('code', {}, request.Tool)),
  request.Reason ? h('p', { className: 'reason' }, request.Reason) : null,
  request.Impact ? h('p', { className: 'reason' }, request.Impact) : null,
  h('p', { className: 'muted' }, 'Allow once affects only this invocation. Always applies only to this tool in this session.'),
  !available.once || !available.session || !available.reject ? h('p', { className: 'muted' }, 'Unavailable choices cannot be represented safely for this request.') : null,
  !canCancel ? h('p', { className: 'muted' }, 'Cancellation Unsupported by this Host; allow or reject remains available.') : null,
  argumentsText ? h('pre', { className: 'arguments' }, argumentsText) : null,
  fields);
}

function questionBody(machineLink, request, isPending) {
  const canCancel = machineLink.supports(Capability.DecisionCancellation);
  const questions = request.Questions.map((question, index) => questionField(request.RequestId, index, question));
  const submit = h('button', { type: 'submit', className: 'primary' }, request.Questions.find((question) => question.ApproveLabel)?.ApproveLabel ?? 'Submit');
  const cancel = h('button', { type: 'button', disabled: !canCancel, title: canCancel ? 'Dismiss this question' : 'Cancellation Unsupported by this Host', onClick: () => answer(true) }, 'Cancel');
  const fields = h('fieldset', {}, questions.map((question) => question.element), !canCancel ? h('p', { className: 'muted' }, 'Cancellation Unsupported by this Host; answering remains available.') : null, h('div', { className: 'actions' }, submit, cancel));
  const form = h('form', {
    className: 'pending-body',
    onSubmit: (event) => { event.preventDefault(); void answer(false); },
    onInput: refreshValidity,
    onChange: refreshValidity,
    onKeydown: (event) => {
      if (event.key === 'Escape') {
        event.preventDefault();
        event.stopPropagation();
        void answer(true);
      }
    },
  }, fields);

  function refreshValidity() {
    submit.disabled = questions.length === 0 || !questions.every((question) => question.validate());
  }

  async function answer(dismissed) {
    if (fields.disabled || !isPending() || (dismissed && !canCancel))
      return;
    refreshValidity();
    if (!dismissed && (submit.disabled || !form.reportValidity()))
      return;
    // Read successful native controls before disabling their fieldset (FormData excludes disabled controls).
    const data = new FormData(form);
    const answers = dismissed ? [] : questions.map((question) => question.answer(data));
    fields.disabled = true;
    try {
      const applied = await machineLink.request(HostProtocol.MethodQuestionRespond, {
        RequestId: request.RequestId,
        Answers: answers,
        Status: dismissed ? QuestionResult.Dismissed : QuestionResult.Answered,
        Reason: dismissed ? QuestionResult.UserCancelled : null,
      });
      if (!applied && isPending()) {
        toast('The Host did not apply this answer; the request is still pending.');
        fields.disabled = false;
        refreshValidity();
      }
    } catch (error) {
      if (isPending()) {
        toast(`Could not answer: ${describe(error)}`, 'error');
        fields.disabled = false;
        refreshValidity();
      }
    }
  }

  refreshValidity();
  return form;
}

/** Native radio/checkbox semantics, with the same values, defaults and custom policy as ChoicePrompt. */
function questionField(requestId, index, question) {
  const name = `q-${requestId}-${index}`;
  const customName = `${name}-custom`;
  const firstDefault = question.Options.findIndex((option) => option.Default && !option.Disabled);
  const options = question.Options.map((option, index) => {
    const input = h('input', {
      type: question.MultiSelect ? 'checkbox' : 'radio', name,
      value: option.Value ?? option.Label, id: `${name}-${index}`,
      disabled: option.Disabled,
      checked: !option.Disabled && option.Default && (question.MultiSelect || index === firstDefault),
    });
    return {
      input,
      element: h('label', { className: 'option', for: `${name}-${index}` }, input,
        h('span', {}, option.Label, option.Description ? h('span', { className: 'muted block' }, option.Description) : null)),
    };
  });
  const custom = question.AllowCustom !== false
    ? h('input', { type: 'text', name: customName, placeholder: 'Other answer', 'aria-label': 'Other answer' })
    : null;
  const element = h('fieldset', { className: 'question' },
    h('legend', {}, question.Header ?? question.Question),
    question.Header ? h('p', {}, question.Question) : null,
    question.Detail ? h('pre', { className: 'muted arguments' }, question.Detail) : null,
    options.map((option) => option.element), custom);
  return {
    element,
    validate: () => {
      const answered = options.some((option) => !option.input.disabled && option.input.checked) || Boolean(custom?.value.trim());
      const control = custom ?? options.find((option) => !option.input.disabled)?.input;
      control?.setCustomValidity(answered ? '' : 'Choose an option or enter an answer.');
      return answered;
    },
    answer: (data) => ({
      Id: question.Id,
      Selected: data.getAll(name),
      Custom: custom ? String(data.get(customName) ?? '').trim() || null : null,
    }),
  };
}
