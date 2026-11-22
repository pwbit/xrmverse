/**
 * Hand-written test controls for the PCF host (never a third-party bundle): a manifest and a bundle per control, as the
 * server serves them under `/Controls/{name}/`. The bundles register themselves through `ComponentFramework.registerControl`
 * as a built control does.
 */

export interface TestControl {
  /** The `customcontrol` row name (publisher prefix + `namespace.constructor`). */
  name: string;
  manifest: string;
  /** Published path to file text. */
  files: Record<string, string>;
}

/** A standard control bound to a Decimal: it shows the value in its container and doubles it on a button click. */
export const standardControl: TestControl = {
  name: 'xv_XvTest.Doubler',
  manifest: `<?xml version="1.0" encoding="utf-8"?>
<manifest>
  <control namespace="XvTest" constructor="Doubler" version="1.2.0" display-name-key="Doubler_Name" description-key="Doubler_Desc" control-type="standard">
    <external-service-usage enabled="false" />
    <property name="value" display-name-key="Value_Name" of-type="Decimal" usage="bound" required="true" />
    <property name="factor" display-name-key="Factor_Name" of-type="Whole.None" usage="input" required="false" default-value="2" />
    <resources>
      <code path="bundle.js" order="1" />
      <css path="css/doubler.css" order="1" />
      <resx path="strings/Doubler.1033.resx" version="1.0.0" />
      <resx path="strings/Doubler.1040.resx" version="1.0.0" />
      <img path="img/icon.png" />
    </resources>
  </control>
</manifest>`,
  files: {
    'bundle.js': `(function () {
  function Doubler() {}
  Doubler.prototype.init = function (context, notify, state, container) {
    this.notify = notify; this.container = container; this.value = context.parameters.value.raw;
  };
  Doubler.prototype.updateView = function (context) {
    this.value = context.parameters.value.raw;
    if (this.container) this.container.textContent = String(this.value);
  };
  Doubler.prototype.getOutputs = function () { return { value: this.value * 2 }; };
  Doubler.prototype.destroy = function () {};
  if (window.ComponentFramework && window.ComponentFramework.registerControl) {
    window.ComponentFramework.registerControl('XvTest.Doubler', Doubler);
  }
})();`,
    'css/doubler.css': '.xv-doubler { color: inherit; }',
    'strings/Doubler.1033.resx': resx({ Doubler_Name: 'Doubler', Doubler_Desc: 'Doubles a number', Value_Name: 'Value' }),
    'strings/Doubler.1040.resx': resx({ Doubler_Name: 'Raddoppia', Value_Name: 'Valore' }),
  },
};

/** A virtual control on the host's React and Fluent UI: it renders a Fluent `Text` with the value. */
export const virtualControl: TestControl = {
  name: 'xv_XvTest.Badge',
  manifest: `<?xml version="1.0" encoding="utf-8"?>
<manifest>
  <control namespace="XvTest" constructor="Badge" version="0.3.1" display-name-key="Badge_Name" control-type="virtual" api-version="1.3.18">
    <property name="value" display-name-key="Value_Name" of-type="Whole.None" usage="bound" required="true" />
    <resources>
      <code path="bundle.js" order="1" />
      <platform-library name="React" version="16.14.0" />
      <platform-library name="Fluent" version="9.46.2" />
    </resources>
  </control>
</manifest>`,
  files: {
    'bundle.js': `(function (React, Fluent) {
  function Badge() {}
  Badge.prototype.init = function (context, notify) { this.notify = notify; };
  Badge.prototype.updateView = function (context) {
    return React.createElement(Fluent.Text, null, 'Badge ' + context.parameters.value.formatted);
  };
  Badge.prototype.getOutputs = function () { return {}; };
  Badge.prototype.destroy = function () {};
  if (window.ComponentFramework && window.ComponentFramework.registerControl) {
    window.ComponentFramework.registerControl('XvTest.Badge', Badge);
  }
})(Reactv16, FluentUIReactv940);`,
  },
};

/**
 * A standard dataset control: it lists the records of its `items` data-set by their formatted `xv_title`, and a button
 * refreshes the data-set.
 */
export const datasetControl: TestControl = {
  name: 'xv_XvTest.TitleList',
  manifest: `<?xml version="1.0" encoding="utf-8"?>
<manifest>
  <control namespace="XvTest" constructor="TitleList" version="1.0.0" display-name-key="TitleList_Name" control-type="standard">
    <data-set name="items" display-name-key="Items_Name" />
    <resources>
      <code path="bundle.js" order="1" />
    </resources>
  </control>
</manifest>`,
  files: {
    'bundle.js': `(function () {
  function TitleList() {}
  TitleList.prototype.init = function (context, notify, state, container) {
    this.container = container;
    this.list = document.createElement('ul');
    this.button = document.createElement('button');
    this.button.textContent = 'Reload list';
    var self = this;
    this.button.onclick = function () { self.items.refresh(); };
    container.appendChild(this.list);
    container.appendChild(this.button);
  };
  TitleList.prototype.updateView = function (context) {
    var items = context.parameters.items;
    this.items = items;
    if (items.loading) return;
    this.list.textContent = '';
    for (var i = 0; i < items.sortedRecordIds.length; i++) {
      var li = document.createElement('li');
      li.textContent = 'Item ' + items.records[items.sortedRecordIds[i]].getFormattedValue('xv_title');
      this.list.appendChild(li);
    }
  };
  TitleList.prototype.getOutputs = function () { return {}; };
  TitleList.prototype.destroy = function () {};
  if (window.ComponentFramework && window.ComponentFramework.registerControl) {
    window.ComponentFramework.registerControl('XvTest.TitleList', TitleList);
  }
})();`,
  },
};

/** A `.resx` document with string entries. */
export function resx(entries: Record<string, string>): string {
  const data = Object.entries(entries)
    .map(([k, v]) => `  <data name="${k}" xml:space="preserve">\n    <value>${v}</value>\n  </data>`)
    .join('\n');
  return `<?xml version="1.0" encoding="utf-8"?>
<root>
  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
${data}
</root>`;
}
