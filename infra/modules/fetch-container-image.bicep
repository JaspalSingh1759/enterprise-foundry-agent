param exists bool
param name string

resource existingApp 'Microsoft.App/containerApps@2023-05-02-preview' existing = if (exists) {
  name: name
}

#disable-next-line BCP318
output containers array = exists ? existingApp.properties.template.containers : []
