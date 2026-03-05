import { getProducts } from './api.js';

document.addEventListener('DOMContentLoaded', async () => {
    try {
        const products = await getProducts();
        const tbody = document.getElementById('products-body');
        tbody.innerHTML = products.map(p => `
            <tr>
                <td>${p.article}</td>
                <td>${p.productTypeName}</td>
                <td>${p.name}</td>
                <td>${p.cost.toFixed(2)}</td>
                <td>${p.rollWidth.toFixed(2)}</td>
                <td>
                    <a href="product-form.html?id=${p.id}" class="edit-link"></a>
                    <a href="product-materials.html?id=${p.id}" class="materials-link"></a>
                </td>
            </tr>
        `).join('');
    } catch (error) {
        alert('Ошибка загрузки продукции: ' + error.message);
    }

    document.getElementById('add-product').addEventListener('click', () => {
        window.location.href = 'product-form.html';
    });
});