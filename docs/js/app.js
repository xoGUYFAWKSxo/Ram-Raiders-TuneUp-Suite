// Screenshot carousel.
document.addEventListener('DOMContentLoaded', () => {
    const galleryTrack = document.getElementById('gallery-track');
    const dots = document.querySelectorAll('.gallery-dot');
    const arrowPrev = document.getElementById('arrow-prev');
    const arrowNext = document.getElementById('arrow-next');
    
    let activeIdx = 0;
    const slideCount = dots.length;
    let carouselInterval;

    function moveGallery(index) {
        if (index < 0) index = slideCount - 1;
        if (index >= slideCount) index = 0;
        
        activeIdx = index;

        if (galleryTrack) {
            galleryTrack.style.transform = `translateX(-${activeIdx * 25}%)`;
        }

        dots.forEach((dot, idx) => {
            dot.classList.toggle('active', idx === activeIdx);
        });
    }

    function startAutoCycle() {
        carouselInterval = setInterval(() => {
            moveGallery(activeIdx + 1);
        }, 8000); // Shift every 8 seconds
    }

    function resetAutoCycle() {
        clearInterval(carouselInterval);
        startAutoCycle();
    }

    dots.forEach((dot, idx) => {
        dot.addEventListener('click', () => {
            moveGallery(idx);
            resetAutoCycle();
        });
    });

    if (arrowPrev) {
        arrowPrev.addEventListener('click', () => {
            moveGallery(activeIdx - 1);
            resetAutoCycle();
        });
    }

    if (arrowNext) {
        arrowNext.addEventListener('click', () => {
            moveGallery(activeIdx + 1);
            resetAutoCycle();
        });
    }

    // Hover pauses automatic sliding
    const galleryViewport = document.querySelector('.gallery-viewport');
    if (galleryViewport) {
        galleryViewport.addEventListener('mouseenter', () => clearInterval(carouselInterval));
        galleryViewport.addEventListener('mouseleave', startAutoCycle);
    }

    startAutoCycle();

});
