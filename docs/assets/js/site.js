// Detection Rule Health docs: copy buttons on code blocks. No tracking, no external calls.
document.addEventListener('DOMContentLoaded', function () {
  document.querySelectorAll('div.highlight, pre:not(div.highlight pre)').forEach(function (block) {
    if (!navigator.clipboard) return;
    var button = document.createElement('button');
    button.type = 'button';
    button.className = 'copy';
    button.textContent = 'Copy';
    button.setAttribute('aria-label', 'Copy to clipboard');
    button.addEventListener('click', function () {
      var code = block.querySelector('code') || block;
      navigator.clipboard.writeText(code.innerText.replace(/\n$/, '')).then(function () {
        button.textContent = 'Copied';
        setTimeout(function () { button.textContent = 'Copy'; }, 1600);
      });
    });
    block.appendChild(button);
  });
});
