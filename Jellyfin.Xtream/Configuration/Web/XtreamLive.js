export default function (view) {
  view.addEventListener("viewshow", () => import(
    ApiClient.getUrl("web/ConfigurationPage", {
      name: "Xtream.js",
    })
  ).then((Xtream) => Xtream.default
  ).then((Xtream) => {
    const pluginId = Xtream.pluginConfig.UniqueId;
    Xtream.setTabs(1);

    const getConfig = ApiClient.getPluginConfiguration(pluginId);
    const visible = view.querySelector("#Visible");
    const timezone = view.querySelector("#CatchupTimeZone");
    const liveCaledonian = view.querySelector("#LiveAtCaledonianTime");
    const startupDelay = view.querySelector("#LiveStartupDelay");
    getConfig.then((config) => {
      visible.checked = config.IsCatchupVisible;
      timezone.value = config.CatchupTimeZoneId ?? '';
      liveCaledonian.checked = config.LiveAtCaledonianTime;
      startupDelay.value = config.LiveStartupDelaySeconds ?? 12;
    });
    const table = view.querySelector('#LiveContent');
    Xtream.populateCategoriesTable(
      table,
      () => getConfig.then((config) => config.LiveTv),
      () => Xtream.fetchJson('Xtream/LiveCategories'),
      (categoryId) => Xtream.fetchJson(`Xtream/LiveCategories/${categoryId}`),
    ).then((data) => {
      view.querySelector('#XtreamLiveForm').addEventListener('submit', (e) => {
        Dashboard.showLoadingMsg();

        ApiClient.getPluginConfiguration(pluginId).then((config) => {
          config.IsCatchupVisible = visible.checked;
          config.CatchupTimeZoneId = timezone.value.trim() || 'Pacific/Noumea';
          config.LiveAtCaledonianTime = liveCaledonian.checked;
          config.LiveStartupDelaySeconds = Math.max(0, Math.min(60, parseInt(startupDelay.value, 10) || 0));
          config.LiveTv = data;
          ApiClient.updatePluginConfiguration(pluginId, config).then((result) => {
            Dashboard.processPluginConfigurationUpdateResult(result);
          });
        });

        e.preventDefault();
        return false;
      });
    }).catch((error) => {
      console.error('Failed to load Live TV categories:', error);
      Dashboard.hideLoadingMsg();
      table.innerHTML = '';
      const errorRow = document.createElement('tr');
      const errorCell = document.createElement('td');
      errorCell.colSpan = 3;
      errorCell.style.color = '#ff6b6b';
      errorCell.style.padding = '16px';
      errorCell.innerHTML = 'Failed to load categories. Please check:<br>' +
        '1. Xtream credentials are configured (Credentials tab)<br>' +
        '2. Xtream server is accessible<br>' +
        '3. Browser console for detailed errors';
      errorRow.appendChild(errorCell);
      table.appendChild(errorRow);
    });
  }));
}