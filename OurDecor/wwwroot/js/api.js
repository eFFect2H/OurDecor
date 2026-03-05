const API_BASE = '/api';

async function fetchJson(url, options = {}) {
    const response = await fetch(url, options);
    if (!response.ok) {
        const error = await response.text();
        throw new Error(error);
    }
    return response.json();
}

export async function getProducts() {
    return fetchJson(`${API_BASE}/products`);
}

export async function getProduct(id) {
    return fetchJson(`${API_BASE}/products/${id}`);
}

export async function createProduct(product) {
    return fetchJson(`${API_BASE}/products`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(product)
    });
}

export async function updateProduct(id, product) {
    return fetchJson(`${API_BASE}/products/${id}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(product)
    });
}

export async function getProductMaterials(productId) {
    return fetchJson(`${API_BASE}/products/${productId}/materials`);
}

export async function getProductTypes() {
    return fetchJson(`${API_BASE}/lookups/product-types`);
}

