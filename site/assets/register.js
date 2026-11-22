// Registration form of /get-started/. No cookies, no storage, no third-party code:
// the form is posted once to the registration service, and the links are shown when it answers 200.
(() => {
  const form = document.getElementById('registration');
  if (!form) return;
  const links = document.getElementById('links');
  const error = form.querySelector('.form-error');
  const button = form.querySelector('button[type="submit"]');
  const loadedAt = Date.now();

  const showError = (message) => {
    error.textContent = message;
    error.hidden = false;
  };

  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    error.hidden = true;
    if (!form.checkValidity()) {
      form.reportValidity();
      return;
    }

    const data = new FormData(form);
    const text = (name) => (data.get(name) ?? '').toString().trim();
    const body = {
      firstName: text('firstName'),
      lastName: text('lastName'),
      email: text('email'),
      company: text('company'),
      country: text('country'),
      jobTitle: text('jobTitle') || null,
      usage: text('usage') || null,
      website: text('website') || null,
      acceptPrivacy: data.get('acceptPrivacy') === 'on',
      acceptTerms: data.get('acceptTerms') === 'on',
      privacyVersion: form.dataset.privacyVersion,
      termsVersion: form.dataset.termsVersion,
      elapsedMs: Date.now() - loadedAt,
    };

    button.disabled = true;
    try {
      const response = await fetch(form.dataset.endpoint, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      });
      if (response.ok) {
        form.hidden = true;
        links.hidden = false;
        links.scrollIntoView({ behavior: 'smooth', block: 'start' });
        return;
      }
      if (response.status === 429) {
        showError('Too many attempts from your network. Please try again in a few minutes.');
      } else if (response.status === 400) {
        const { errors = [] } = await response.json().catch(() => ({}));
        showError(errors.length ? `Please check the form: ${errors.join('; ')}.` : 'Please check the form.');
      } else {
        showError('The registration could not be saved. Please try again later or write to support@powerbuilders.it.');
      }
    } catch {
      showError('The registration service cannot be reached. Please try again later or write to support@powerbuilders.it.');
    } finally {
      button.disabled = false;
    }
  });
})();
